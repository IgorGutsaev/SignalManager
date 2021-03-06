﻿namespace EventsGateway.Gateway
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Diagnostics;
    using EventsGateway.Common;
    using EventsGateway.Common.Threading;
    using EventsManager.CloudEventHub.Gateway.Models;

    public class BatchSenderThread<TQueueItem> : EventProcessor
        where TQueueItem: IQueuedItem
    {
        private static readonly string _logMessagePrefix = "BatchSenderThread error. ";
        private readonly object _syncRoot = new object();

        private readonly IAsyncQueue<TQueueItem> _dataSource;
        private readonly IMessageSender<TQueueItem> _dataTarget;
        private readonly Func<TQueueItem, string> _serializedData;
        private Thread _worker;
        private AutoResetEvent _operational;
        private AutoResetEvent _doWork;
        private bool _running;
        private int _outstandingTasks;

        public BatchSenderThread(IAsyncQueue<TQueueItem> dataSource, IMessageSender<TQueueItem> dataTarget, Func<TQueueItem, string> serializedData, ILogger logger)
            : base(SafeLogger.FromLogger(logger))
        {
            if (dataSource == null || dataTarget == null)
            {
                throw new ArgumentException("data source and data target cannot be null");
            }

            _operational = new AutoResetEvent(false);
            _doWork = new AutoResetEvent(false);
            _running = false;
            _dataSource = dataSource;
            _dataTarget = dataTarget;
            _serializedData = serializedData;
            _outstandingTasks = 0;
        }

        public override bool Start()
        {
            bool start = false;

            lock (_syncRoot)
            {
                if (_running == false)
                {
                    start = true;
                }
            }

            if (start)
            {
                _worker = new Thread(ThreadJob);
                _running = true;
                _worker.Start();
                return _operational.WaitOne();
            }

            return false;
        }

        public override bool Stop(int timeout)
        {
            bool stop = false;

            lock (_syncRoot)
            {
                if (_running == true)
                {
                    // There must exist a worker thread
                    System.Diagnostics.Debug.Assert(_worker != null);

                    // signal the worker thread that exit is impending
                    _running = false;
                    _doWork.Set();

                    stop = true;
                }
            }

            if (stop)
            {
                if (_operational.WaitOne(timeout) == false)
                {
                    // no other choice than forcing a stop
                    _worker.Abort();
                }

                _worker.Join();

                return true;
            }

            return false;
        }

        public override void Process()
        {
            _doWork.Set();
        }

        public event EventBatchProcessedEventHandler OnEventsBatchProcessed;

        private void ThreadJob()
        {
            // signal that the worker thread has actually started processing the events
            _operational.Set();

            try
            {
                const int WAIT_TIMEOUT = 50; // milliseconds

                // run until Stop() is called
                while (_running == true)
                {
                    try
                    {
                        // If there are no tasks to be served, wait for some events to process
                        // Use a timeout to prevent race conditions on the outstanding tasks count
                        // and the actual queue count
                        _doWork.WaitOne(WAIT_TIMEOUT);

                        _logger.Flush();

                        // Fish from the queue and accumulate, keep track of outstanding tasks to 
                        // avoid accumulating too many competing tasks. Note that we are going to schedule
                        // one more tasks than strictly needed, so that we prevent tasks to sit in the queue
                        // because of the race condition on the outstanding task count (_outstandingTasks) 
                        // and the tasks actually sitting in the queue.  (*)
                        // To prevent this race condition, we will wait with a timeout
                        int count = _dataSource.Count - _outstandingTasks;

                        if (count == 0)
                        {
                            continue;
                        }

                        // check if we have been woken up to actually stop processing 
                        EventBatchProcessedEventHandler eventBatchProcessed = null;

                        lock (_syncRoot)
                        {
                            if (_running == false)
                            {
                                return;
                            }

                            // take a snapshot of event handlers to invoke
                            eventBatchProcessed = OnEventsBatchProcessed;
                        }

                        // allocate a container to keep track of tasks for events in the queue
                        var tasks = new List<TaskWrapper>();

                        // process all messages that have not been processed yet 
                        while (--count >= 0)
                        {
                            TaskWrapper<OperationStatus<TQueueItem>> t = null;

                            try
                            {
                                t = _dataSource.TryPop();
                            }
                            catch
                            {
                                Interlocked.Decrement(ref _outstandingTasks);

                                continue;
                            }

                            // increment outstanding task count 
                            Interlocked.Increment(ref _outstandingTasks);

                            t.ContinueWith<TaskWrapper>(popped =>
                            {
                                // Decrement the numbers of outstanding tasks. 
                                // (*) Note that there is a race  condition because at this point in time the tasks 
                                // is already out of the queue but we did not decrement the outstanding task count 
                                // yet. This race condition may cause tasks to be left sitting in the queue. 
                                // To deal with this race condition, we will wait with a timeout
                                Interlocked.Decrement(ref _outstandingTasks);

                                // because the outstanding task counter is incremented before 
                                // adding, we should never incur a negative count 
                                Debug.Assert(_outstandingTasks >= 0);

                                if (popped?.Result != null && popped.Result.IsSuccess)
                                {
                                    return _dataTarget.SendMessage(popped.Result.Result.GetDeviceId(), popped.Result.Result);
                                }

                                return null;
                            });

                            AddToProcessed(tasks, t);
                        }

                        // alert any client about outstanding message tasks
                        if (eventBatchProcessed != null)
                        {
                            var sh = new SafeAction<List<TaskWrapper>>(allScheduledTasks => eventBatchProcessed(allScheduledTasks), Logger);

                            TaskWrapper.Run(() => sh.SafeInvoke(tasks));
                        }
                    }
                    catch (StackOverflowException ex) // do not hide stack overflow exceptions
                    {
                        Logger.LogError(_logMessagePrefix + ex.Message);
                        throw;
                    }
                    catch (OutOfMemoryException ex) // do not hide memory exceptions
                    {
                        Logger.LogError(_logMessagePrefix + ex.Message);
                        throw;
                    }
                    catch (Exception ex) // catch all other exceptions
                    {
                        Logger.LogError(_logMessagePrefix + ex.Message);
                    }
                }
            }
            finally
            {
                _operational.Set();
            }
        }

        private void AddToProcessed(List<TaskWrapper> tasks, TaskWrapper<OperationStatus<TQueueItem>> t)
        {
            try
            {
                tasks.Add(t);
            }
            catch (StackOverflowException)// do not hide stack overflow exceptions
            {
                throw;
            }
            catch (OutOfMemoryException) // do not hide memory exceptions
            {
                throw;
            }
            catch (Exception ex) // catch all other exceptions
            {
                Logger.LogError("Exception on adding task: " + ex.Message);

                // TODO
                // If we are here, the task that has been popped could not be added to the list
                // of tasks that the client will be notifed about
                // This does not mean that the task has not been processed though
            }
        }
    }
}