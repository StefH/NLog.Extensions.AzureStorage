﻿using System;
using System.Collections.Generic;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using NLog.Common;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;

namespace NLog.Extensions.AzureStorage
{
    /// <summary>
    /// Azure Table Storage NLog Target
    /// </summary>
    /// <seealso cref="NLog.Targets.TargetWithLayout" />
    [Target("AzureTableStorage")]
    public sealed class TableStorageTarget : TargetWithLayout
    {
        private CloudTableClient _client;
        private CloudTable _table;
        private string _machineName;
        private readonly AzureStorageNameCache _containerNameCache = new AzureStorageNameCache();
        private readonly Func<string, string> _checkAndRepairTableNameDelegate;

        //Delegates for bucket sorting
        private SortHelpers.KeySelector<AsyncLogEventInfo, TablePartitionKey> _getTablePartitionNameDelegate;
        struct TablePartitionKey : IEquatable<TablePartitionKey>
        {
            public readonly string TableName;
            public readonly string PartitionId;

            public TablePartitionKey(string tableName, string partitionId)
            {
                TableName = tableName;
                PartitionId = partitionId;
            }

            public bool Equals(TablePartitionKey other)
            {
                return TableName == other.TableName &&
                       PartitionId == other.PartitionId;
            }

            public override bool Equals(object obj)
            {
                return (obj is TablePartitionKey) && Equals((TablePartitionKey)obj);
            }

            public override int GetHashCode()
            {
                return TableName.GetHashCode() ^ PartitionId.GetHashCode();
            }
        }

        public string ConnectionString { get => (_connectionString as SimpleLayout)?.Text ?? null; set => _connectionString = value; }
        private Layout _connectionString;
        public string ConnectionStringKey { get; set; }

        [RequiredParameter]
        public Layout TableName { get; set; }

        public string LogTimeStampFormat { get; set; } = "O";

        public TableStorageTarget()
        {
            OptimizeBufferReuse = true;
            _checkAndRepairTableNameDelegate = CheckAndRepairTableNamingRules;
        }

        /// <summary>
        /// Initializes the target. Can be used by inheriting classes
        /// to initialize logging.
        /// </summary>
        protected override void InitializeTarget()
        {
            base.InitializeTarget();

            _machineName = GetMachineName();

            string connectionString = string.Empty;
            try
            {
                connectionString = ConnectionStringHelper.LookupConnectionString(_connectionString, ConnectionStringKey);
                _client = CloudStorageAccount.Parse(connectionString).CreateCloudTableClient();
                InternalLogger.Trace("AzureTableStorageTarget - Initialized");
            }
            catch (Exception ex)
            {
                InternalLogger.Error(ex, "AzureTableStorageTarget(Name={0}): Failed to create TableClient with connectionString={1}.", Name, connectionString);
                throw;
            }
        }

        /// <summary>
        /// Writes logging event to the log target.
        /// classes.
        /// </summary>
        /// <param name="logEvent">Logging event to be written out.</param>
        protected override void Write(LogEventInfo logEvent)
        {
            if (String.IsNullOrEmpty(logEvent.Message))
                return;

            var tableName = RenderLogEvent(TableName, logEvent);
            try
            {
                tableName = CheckAndRepairTableName(tableName);

                InitializeTable(tableName);
                var layoutMessage = RenderLogEvent(Layout, logEvent);
                var entity = new NLogEntity(logEvent, layoutMessage, _machineName, logEvent.LoggerName, LogTimeStampFormat);
                var insertOperation = TableOperation.Insert(entity);
                TableExecute(_table, insertOperation);
            }
            catch (StorageException ex)
            {
                InternalLogger.Error(ex, "AzureTableStorageTarget: failed writing to table: {0}", tableName);
                throw;
            }
        }

        /// <summary>
        /// Writes an array of logging events to the log target. By default it iterates on all
        /// events and passes them to "Write" method. Inheriting classes can use this method to
        /// optimize batch writes.
        /// </summary>
        /// <param name="logEvents">Logging events to be written out.</param>
        protected override void Write(IList<AsyncLogEventInfo> logEvents)
        {
            if (logEvents.Count <= 1)
            {
                base.Write(logEvents);
                return;
            }

            //must sort into containers and then into the blobs for the container
            if (_getTablePartitionNameDelegate == null)
                _getTablePartitionNameDelegate = c => new TablePartitionKey(RenderLogEvent(TableName, c.LogEvent), c.LogEvent.LoggerName ?? string.Empty);

            var partitionBuckets = SortHelpers.BucketSort(logEvents, _getTablePartitionNameDelegate);

            //Iterate over all the tables being written to
            foreach (var partitionBucket in partitionBuckets)
            {
                var tableName = partitionBucket.Key.TableName;

                try
                {
                    tableName = CheckAndRepairTableName(tableName);

                    InitializeTable(tableName);

                    //iterate over all the partition keys or we will get a System.ArgumentException: 'All entities in a given batch must have the same partition key.'
                    var batch = new TableBatchOperation();
                    //add each message for the destination table partition limit batch to 100 elements
                    foreach (var asyncLogEventInfo in partitionBucket.Value)
                    {
                        var layoutMessage = RenderLogEvent(Layout, asyncLogEventInfo.LogEvent);
                        var entity = new NLogEntity(asyncLogEventInfo.LogEvent, layoutMessage, _machineName, partitionBucket.Key.PartitionId, LogTimeStampFormat);
                        batch.Insert(entity);
                        if (batch.Count == 100)
                        {
                            TableExecuteBatch(_table, batch);
                            batch.Clear();
                        }
                    }

                    if (batch.Count > 0)
                        TableExecuteBatch(_table, batch);

                    foreach (var asyncLogEventInfo in partitionBucket.Value)
                        asyncLogEventInfo.Continuation(null);
                }
                catch (StorageException ex)
                {
                    InternalLogger.Error(ex, "AzureTableStorageTarget: failed writing batch to table: {0}", tableName);
                    throw;
                }
            }
        }

        private static void TableExecute(CloudTable cloudTable, TableOperation insertOperation)
        {
#if NETSTANDARD
            cloudTable.ExecuteAsync(insertOperation).GetAwaiter().GetResult();
#else
            cloudTable.Execute(insertOperation);
#endif
        }

        private static void TableExecuteBatch(CloudTable cloudTable, TableBatchOperation batch)
        {
#if NETSTANDARD
            cloudTable.ExecuteBatchAsync(batch).GetAwaiter().GetResult();
#else
            cloudTable.ExecuteBatch(batch);
#endif
        }

        private void TableCreateIfNotExists(CloudTable cloudTable)
        {
#if NETSTANDARD
            cloudTable.CreateIfNotExistsAsync().GetAwaiter().GetResult();
#else
            cloudTable.CreateIfNotExists();
#endif
        }

        /// <summary>
        /// Initializes the Azure storage table and creates it if it doesn't exist.
        /// </summary>
        /// <param name="tableName">Name of the table.</param>
        private void InitializeTable(string tableName)
        {
            if (_table == null || _table.Name != tableName)
            {
                _table = _client.GetTableReference(tableName);
                try
                {
                    TableCreateIfNotExists(_table);
                }
                catch (StorageException storageException)
                {
                    InternalLogger.Error(storageException, "AzureTableStorageTarget: failed to get a reference to storage table.");
                    throw;
                }
            }
        }

        private string CheckAndRepairTableName(string tableName)
        {
            return _containerNameCache.LookupStorageName(tableName, _checkAndRepairTableNameDelegate);
        }

        private string CheckAndRepairTableNamingRules(string tableName)
        {
            InternalLogger.Trace("AzureTableStorageTarget(Name={0}): Requested Table Name: {1}", Name, tableName);
            string validTableName = AzureStorageNameCache.CheckAndRepairTableNamingRules(tableName);
            if (validTableName == tableName)
            {
                InternalLogger.Trace("AzureTableStorageTarget(Name={0}): Using Table Name: {0}", Name, validTableName);
            }
            else
            {
                InternalLogger.Trace("AzureTableStorageTarget(Name={0}): Using Cleaned Table name: {0}", Name, validTableName);
            }
            return validTableName;
        }

        /// <summary>
        /// Gets the machine name
        /// </summary>
        private static string GetMachineName()
        {
            return TryLookupValue(() => Environment.GetEnvironmentVariable("COMPUTERNAME"), "COMPUTERNAME")
                ?? TryLookupValue(() => Environment.GetEnvironmentVariable("HOSTNAME"), "HOSTNAME")
#if !NETSTANDARD1_3
                ?? TryLookupValue(() => Environment.MachineName, "MachineName")
#endif
                ?? TryLookupValue(() => System.Net.Dns.GetHostName(), "DnsHostName");
        }

        private static string TryLookupValue(Func<string> lookupFunc, string lookupType)
        {
            try
            {
                string lookupValue = lookupFunc()?.Trim();
                return string.IsNullOrEmpty(lookupValue) ? null : lookupValue;
            }
            catch (Exception ex)
            {
                InternalLogger.Warn(ex, "AzureTableStorageTarget: Failed to lookup {0}", lookupType);
                return null;
            }
        }
    }
}
