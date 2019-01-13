﻿using Common.Logging;
using Microsoft.Azure.ServiceBus;
using SlimMessageBus.Host.Config;

namespace SlimMessageBus.Host.AzureServiceBus.Consumer
{
    public class QueueConsumer : BaseConsumer
    {
        private readonly IQueueClient _queueClient;

        public QueueConsumer(ServiceBusMessageBus messageBus, ConsumerSettings consumerSettings) 
            : base(messageBus, consumerSettings,
                messageBus.ServiceBusSettings.QueueClientFactory(consumerSettings.Topic),
                LogManager.GetLogger<QueueConsumer>())
        {
            _queueClient = (IQueueClient) Client;
        }

        #region IDisposable

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _queueClient.CloseAsync().GetAwaiter().GetResult();
            }
        }

        #endregion
    }
}