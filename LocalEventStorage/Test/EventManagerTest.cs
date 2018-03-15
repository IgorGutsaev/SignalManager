﻿using EventsManager.Abstractions;
using EventsManager.LocalEventStorage.Abstractions;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Xunit;

namespace EventsManager.LocalEventStorage.Test
{
    public class EventManagerTest : BaseTest
    {
        [Fact]
        public void Test_EventManager()
        {
            // Prepare
            IEventManager manager = this.GetService<IEventManager>();
            ISignalService signals = this.GetService<ISignalService>();
            signals.Truncate();
            int countBefore = signals.Count();


            // Pre-validate

            // Perform
            List<string> data = new List<string>();
            for (int i = 0; i < 1000; i++)
                data.Add("{ guid: " + Guid.NewGuid().ToString() + "}");

            foreach (string signal in data)
            {
                manager.RegisterEvent(Signal.New("fooDevice", signal, DateTime.Now));
                Thread.Sleep(5);
            }
            Thread.Sleep(1000 * (manager.IntervalMemToLocal + 1)); // Wait while manager save data to the end

            // Post-validate
            int countAfter = signals.Count();
            Assert.Equal(countBefore + data.Count, countAfter);
        }
    }
}