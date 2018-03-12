namespace EventsGateway.Test
{
    using System;
    using System.Threading;
    using EventsGateway.Common;
    using EventsGateway.Gateway;

    //--//

    public class RealDataTest : ITest
    {
        private readonly ILogger                                    _logger;
        private readonly AutoResetEvent                             _completed;
        private readonly GatewayQueue<QueuedItem>                   _gatewayQueue;
        private readonly IMessageSender<QueuedItem>                 _sender;
        private readonly BatchSenderThread<QueuedItem, QueuedItem>  _batchSenderThread;
        private          int                                        _totalMessagesSent;
        private          int                                        _totalMessagesToSend;

        //--//

        public RealDataTest(ILogger logger)
        {
            if( logger == null )
            {
                throw new ArgumentException( "Cannot run tests without logging" );
            }

             _completed = new AutoResetEvent( false );

            _logger = logger;

            _totalMessagesSent = 0;
            _totalMessagesToSend = 0;
            _gatewayQueue = new GatewayQueue<QueuedItem>( );            

            _sender = new MockSender<QueuedItem>( this );

            _batchSenderThread = new BatchSenderThread<QueuedItem, QueuedItem>( _gatewayQueue, _sender, m => m, null, _logger );
        }

        public void Run( )
        {
            TestRealTimeData( );
        }

        public void TestRealTimeData( )
        {
            const int INITIAL_MESSAGES_BOUND = 5;
            const int STOP_TIMEOUT_MS = 5000; // ms

            try
            {
                ////IList<string> sources = Loader.GetSources( ).Where(
                ////    m => !m.Contains( "Mock" )
                ////        && ( m.Contains( "Socket" ) || m.Contains( "SerialPort" ) )
                ////    ).ToList( );

                ////IList<SensorEndpoint> endpoints = Loader.GetEndpoints( );

                ////if( !endpoints.Any( m => m.Name.Contains( "Socket" ) ) )
                ////{
                ////    Console.Out.WriteLine( "Need to specify local ip host for Socket interations " +
                ////                        "and name of endpoint should contain \"Socket\"" );
                ////}

                GatewayService service = PrepareGatewayService( );

                ////DeviceAdapterLoader dataIntakeLoader = new DeviceAdapterLoader(
                ////    sources,
                ////    endpoints,
                ////    _logger );

                _totalMessagesToSend += INITIAL_MESSAGES_BOUND;

                ////dataIntakeLoader.StartAll( service.Enqueue, DataArrived );

                _completed.WaitOne( );

                ////dataIntakeLoader.StopAll( );

                _batchSenderThread.Stop( STOP_TIMEOUT_MS );
            }
            catch( Exception ex )
            {
                _logger.LogError( "exception caught: " + ex.StackTrace );
            }
            finally
            {
                _batchSenderThread.Stop( STOP_TIMEOUT_MS );
                _sender.Close( );
            }
        }

        public int TotalMessagesSent
        {
            get
            {
                return _totalMessagesSent;
            }
        }

        public int TotalMessagesToSend
        {
            get
            {
                return _totalMessagesToSend;
            }
        }

        public void Completed( )
        {
            _completed.Set( );

            Console.WriteLine( String.Format( "Test completed, {0} messages sent", _totalMessagesToSend ) );
        }

        private GatewayService PrepareGatewayService( )
        {
            _batchSenderThread.Start( );

            GatewayService service = new GatewayService( _gatewayQueue, _batchSenderThread,
                m => DataTransforms.QueuedItemFromSensorDataContract(
                        DataTransforms.AddTimeCreated( DataTransforms.SensorDataContractFromString( m, _logger ) ), _logger ) );

            service.Logger = _logger;
            service.OnDataInQueue += DataInQueue;

            return service;
        }

        protected void DataArrived( string data )
        {
            _totalMessagesSent++;

            //we dont want to stop, so need to increment a limit
            _totalMessagesToSend++;

            string logMessage = "New data arrived: " + data;
            Console.Out.WriteLine( logMessage );
            _logger.LogInfo( logMessage );
        }

        protected virtual void DataInQueue( QueuedItem data )
        {
            // LORENZO: test behaviours such as accumulating data an processing in batch
            // as it stands, we are processing every event as it comes in

            _batchSenderThread.Process( );
        }
    }
}