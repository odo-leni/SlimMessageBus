﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Common.Logging;
using Microsoft.Azure.ServiceBus;
using SlimMessageBus.Host.AzureServiceBus.Config;
using SlimMessageBus.Host.AzureServiceBus.Consumer;
using SlimMessageBus.Host.Collections;
using SlimMessageBus.Host.Config;

namespace SlimMessageBus.Host.AzureServiceBus
{
    public class ServiceBusMessageBus : MessageBusBase
    {
        private static readonly ILog Log = LogManager.GetLogger<ServiceBusMessageBus>();

        public ServiceBusMessageBusSettings ServiceBusSettings { get; }

        private readonly SafeDictionaryWrapper<string, TopicClient> _producerByTopic;
        private readonly SafeDictionaryWrapper<string, QueueClient> _producerByQueue;

        private readonly List<BaseConsumer> _consumers = new List<BaseConsumer>();

        private readonly IDictionary<string, PathKind> _kindByTopic = new Dictionary<string, PathKind>();
        private readonly IDictionary<Type, PathKind> _kindByMessageType = new Dictionary<Type, PathKind>();

        public ServiceBusMessageBus(MessageBusSettings settings, ServiceBusMessageBusSettings serviceBusSettings) : base(settings)
        {
            ServiceBusSettings = serviceBusSettings;

            foreach (var producerSettings in settings.Producers)
            {
                var producerKind = producerSettings.GetKind();
                PathKind existingKind;

                var topic = producerSettings.DefaultTopic;
                if (topic != null)
                {
                    if (_kindByTopic.TryGetValue(topic, out existingKind))
                    {
                        if (existingKind != producerKind)
                        {
                            throw new InvalidConfigurationMessageBusException($"The same name '{topic}' was used for queue and topic. You cannot share one name for a topic and queue. Please fix your configuration.");
                        }
                    }
                    else
                    {
                        _kindByTopic.Add(topic, producerKind);
                    }
                }

                if (_kindByMessageType.TryGetValue(producerSettings.MessageType, out existingKind))
                {
                    if (existingKind != producerKind)
                    {
                        throw new InvalidConfigurationMessageBusException($"The same message type '{producerSettings.MessageType}' was used for queue and topic. You cannot share one message type for a topic and queue. Please fix your configuration.");
                    }
                }
                else
                {
                    _kindByMessageType.Add(producerSettings.MessageType, producerKind);
                }
            }

            _producerByTopic = new SafeDictionaryWrapper<string, TopicClient>(topic =>
            {
                Log.DebugFormat(CultureInfo.InvariantCulture, "Creating TopicClient for path {0}", topic);
                return serviceBusSettings.TopicClientFactory(topic);
            });

            _producerByQueue = new SafeDictionaryWrapper<string, QueueClient>(queue =>
            {
                Log.DebugFormat(CultureInfo.InvariantCulture, "Creating QueueClient for path {0}", queue);
                return serviceBusSettings.QueueClientFactory(queue);
            });

            Log.Info("Creating consumers");
            foreach (var consumerSettings in settings.Consumers)
            {
                Log.InfoFormat(CultureInfo.InvariantCulture, "Creating consumer for {0}", consumerSettings.FormatIf(Log.IsInfoEnabled));

                var consumer = consumerSettings.GetKind() == PathKind.Topic
                    ? new TopicSubscriptionConsumer(this, consumerSettings) as BaseConsumer
                    : new QueueConsumer(this, consumerSettings);

                _consumers.Add(consumer);
            }
        }

        #region Overrides of MessageBusBase

        protected override void Dispose(bool disposing)
        {
            if (_consumers.Count > 0)
            {
                _consumers.ForEach(c => c.DisposeSilently("Consumer", Log));
                _consumers.Clear();
            }

            if (_producerByQueue.Dictonary.Count > 0)
            {
                Task.WaitAll(_producerByQueue.Dictonary.Values.Select(x =>
                {
                    Log.DebugFormat(CultureInfo.InvariantCulture, "Closing QueueClient for path {0}", x.Path);
                    return x.CloseAsync();
                }).ToArray());

                _producerByQueue.Clear();
            }

            if (_producerByTopic.Dictonary.Count > 0)
            {
                Task.WaitAll(_producerByTopic.Dictonary.Values.Select(x =>
                {
                    Log.DebugFormat(CultureInfo.InvariantCulture, "Closing TopicClient for path {0}", x.Path);
                    return x.CloseAsync();
                }).ToArray());

                _producerByTopic.Clear();
            }

            base.Dispose(disposing);
        }

        public override async Task PublishToTransport(Type messageType, object message, string topic, byte[] payload)
        {
            AssertActive();

            // determine the SMB topic name if its a Azure SB queue or topic
            if (!_kindByTopic.TryGetValue(topic, out var kind))
            {
                if (!_kindByMessageType.TryGetValue(messageType, out kind))
                {
                    // by default this will be a topic
                    kind = PathKind.Topic;
                }
            }

            Log.DebugFormat(CultureInfo.InvariantCulture, "Producing message {0} of type {1} on {2} {3} with size {4}", message, messageType.Name, kind, topic, payload.Length);

            var m = new Message(payload);
            // ToDo: add support for partitioning key
            // ToDo: add support for user properties
            // ToDo: add support for expiration

            if (kind == PathKind.Topic)
            {
                var topicProducer = _producerByTopic.GetOrAdd(topic);
                await topicProducer.SendAsync(m).ConfigureAwait(false);
            }
            else
            {
                var queueProducer = _producerByQueue.GetOrAdd(topic);
                await queueProducer.SendAsync(m).ConfigureAwait(false);
            }

            Log.DebugFormat(CultureInfo.InvariantCulture, "Delivered message {0} of type {1} on {2} {3}", message, messageType.Name, kind, topic);
        }

        #endregion
    }
}
