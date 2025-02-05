﻿// This source code is dual-licensed under the Apache License, version
// 2.0, and the Mozilla Public License, version 2.0.
// Copyright (c) 2007-2020 VMware, Inc.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace RabbitMQ.Stream.Client
{
    public record StreamSystemConfig : INamedEntity
    {
        public string UserName { get; set; } = "guest";
        public string Password { get; set; } = "guest";
        public string VirtualHost { get; set; } = "/";
        public TimeSpan Heartbeat { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// TLS options setting.
        /// </summary>
        public SslOption Ssl { get; set; } = new SslOption();

        public IList<EndPoint> Endpoints { get; set; } = new List<EndPoint> { new IPEndPoint(IPAddress.Loopback, 5552) };

        public AddressResolver AddressResolver { get; set; } = null;
        public string ClientProvidedName { get; set; } = "dotnet-stream-locator";
    }

    public class StreamSystem
    {
        private readonly ClientParameters clientParameters;
        private Client client;

        private StreamSystem(ClientParameters clientParameters, Client client)
        {
            this.clientParameters = clientParameters;
            this.client = client;
        }

        public bool IsClosed => client.IsClosed;

        public static async Task<StreamSystem> Create(StreamSystemConfig config)
        {
            var clientParams = new ClientParameters
            {
                UserName = config.UserName,
                Password = config.Password,
                VirtualHost = config.VirtualHost,
                Ssl = config.Ssl,
                AddressResolver = config.AddressResolver,
                ClientProvidedName = config.ClientProvidedName,
                Heartbeat = config.Heartbeat
            };
            // create the metadata client connection
            foreach (var endPoint in config.Endpoints)
            {
                try
                {
                    var client = await Client.Create(clientParams with { Endpoint = endPoint });
                    if (!client.IsClosed)
                    {
                        return new StreamSystem(clientParams, client);
                    }
                }
                catch (Exception e)
                {
                    if (e is ProtocolException or SslException)
                    {
                        throw;
                    }

                    //TODO log? 
                }
            }

            throw new StreamSystemInitialisationException("no endpoints could be reached");
        }

        public async Task Close()
        {
            await client.Close("system close");
        }

        private readonly SemaphoreSlim _semClientProvidedName = new(1);

        private async Task MayBeReconnectLocator()
        {
            try
            {
                await _semClientProvidedName.WaitAsync();
                {
                    if (client.IsClosed)
                    {
                        client = await Client.Create(client.Parameters with
                        {
                            ClientProvidedName = clientParameters.ClientProvidedName
                        });
                    }
                }
            }
            finally
            {
                _semClientProvidedName.Release();
            }
        }

        public async Task<Producer> CreateProducer(ProducerConfig producerConfig)
        {
            // Validate the ProducerConfig values
            if (producerConfig.Stream == "")
            {
                throw new CreateProducerException($"Stream name can't be empty");
            }

            if (producerConfig.MessagesBufferSize < Consts.MinBatchSize)
            {
                throw new CreateProducerException(
                    $"Batch Size must be bigger than 0");
            }

            await MayBeReconnectLocator();
            var meta = await client.QueryMetadata(new[] { producerConfig.Stream });
            var metaStreamInfo = meta.StreamInfos[producerConfig.Stream];
            if (metaStreamInfo.ResponseCode != ResponseCode.Ok)
            {
                throw new CreateProducerException($"producer could not be created code: {metaStreamInfo.ResponseCode}");
            }

            try
            {
                await _semClientProvidedName.WaitAsync();

                return await Producer.Create(
                    clientParameters with { ClientProvidedName = producerConfig.ClientProvidedName },
                    producerConfig, metaStreamInfo);
            }
            finally
            {
                _semClientProvidedName.Release();
            }
        }

        public async Task CreateStream(StreamSpec spec)
        {
            var response = await client.CreateStream(spec.Name, spec.Args);
            if (response.ResponseCode is ResponseCode.Ok or ResponseCode.StreamAlreadyExists)
            {
                return;
            }

            throw new CreateStreamException($"Failed to create stream, error code: {response.ResponseCode.ToString()}");
        }

        public async Task<bool> StreamExists(string stream)
        {
            var streams = new[] { stream };
            var response = await client.QueryMetadata(streams);
            return response.StreamInfos is { Count: >= 1 } &&
                   response.StreamInfos[stream].ResponseCode == ResponseCode.Ok;
        }

        private static void MaybeThrowQueryException(string reference, string stream)
        {
            if (string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(stream))
            {
                throw new QueryException("Stream name and reference can't be empty or null");
            }
        }

        /// <summary>
        /// QueryOffset retrieves the last consumer offset stored
        /// given a consumer name and stream name 
        /// </summary>
        /// <param name="reference">Consumer name</param>
        /// <param name="stream">Stream name</param>
        /// <returns></returns>
        public async Task<ulong> QueryOffset(string reference, string stream)
        {
            MaybeThrowQueryException(reference, stream);

            var response = await client.QueryOffset(reference, stream);
            ClientExceptions.MaybeThrowException(response.ResponseCode,
                $"QueryOffset stream: {stream}, reference: {reference}");
            return response.Offset;
        }

        /// <summary>
        /// QuerySequence retrieves the last producer sequence
        /// given a producer name and stream 
        /// </summary>
        /// <param name="reference">Producer name</param>
        /// <param name="stream">Stream name</param>
        /// <returns></returns>
        public async Task<ulong> QuerySequence(string reference, string stream)
        {
            await MayBeReconnectLocator();
            MaybeThrowQueryException(reference, stream);
            var response = await client.QueryPublisherSequence(reference, stream);
            ClientExceptions.MaybeThrowException(response.ResponseCode,
                $"QuerySequence stream: {stream}, reference: {reference}");
            return response.Sequence;
        }

        public async Task DeleteStream(string stream)
        {
            await MayBeReconnectLocator();
            var response = await client.DeleteStream(stream);
            if (response.ResponseCode == ResponseCode.Ok)
            {
                return;
            }

            throw new DeleteStreamException($"Failed to delete stream, error code: {response.ResponseCode.ToString()}");
        }

        public async Task<Consumer> CreateConsumer(ConsumerConfig consumerConfig)
        {
            await MayBeReconnectLocator();
            var meta = await client.QueryMetadata(new[] { consumerConfig.Stream });
            var metaStreamInfo = meta.StreamInfos[consumerConfig.Stream];
            if (metaStreamInfo.ResponseCode != ResponseCode.Ok)
            {
                throw new CreateConsumerException($"consumer could not be created code: {metaStreamInfo.ResponseCode}");
            }

            try
            {
                await _semClientProvidedName.WaitAsync();
                var s = clientParameters with { ClientProvidedName = consumerConfig.ClientProvidedName };
                return await Consumer.Create(s,
                    consumerConfig, metaStreamInfo);
            }
            finally
            {
                _semClientProvidedName.Release();
            }
        }
    }

    public class CreateConsumerException : Exception
    {
        public CreateConsumerException(string s) : base(s)
        {
        }
    }

    public class CreateStreamException : Exception
    {
        public CreateStreamException(string s) : base(s)
        {
        }
    }

    public class DeleteStreamException : Exception
    {
        public DeleteStreamException(string s) : base(s)
        {
        }
    }

    public class QueryException : Exception
    {
        public QueryException(string s) : base(s)
        {
        }
    }

    public class CreateProducerException : Exception
    {
        public CreateProducerException(string s) : base(s)
        {
        }
    }

    public readonly struct LeaderLocator
    {
        private readonly string value;

        private LeaderLocator(string value)
        {
            this.value = value;
        }

        public static LeaderLocator ClientLocal => new LeaderLocator("client-local");
        public static LeaderLocator Random => new LeaderLocator("random");
        public static LeaderLocator LeastLeaders => new LeaderLocator("least-leaders");

        public override string ToString()
        {
            return value;
        }
    }

    public class StreamSystemInitialisationException : Exception
    {
        public StreamSystemInitialisationException(string error) : base(error)
        {
        }
    }
}
