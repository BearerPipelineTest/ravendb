using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Raven.Client.Documents.Indexes;
using Raven.Client.ServerWide;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Auto;
using Raven.Server.Documents.Indexes.MapReduce.Auto;
using Raven.Server.Rachis;
using Raven.Server.Utils;
using Sparrow.Json.Parsing;

namespace Raven.Server.ServerWide.Commands.Indexes
{
    public class PutAutoIndexCommand : UpdateDatabaseCommand
    {
        public DateTime CreatedAt { get; set; }
        public AutoIndexDefinition Definition;

        public PutAutoIndexCommand()
        {
        }

        public PutAutoIndexCommand(AutoIndexDefinition definition, string databaseName, string uniqueRequestId, DateTime? createdAt = null)
            : base(databaseName, uniqueRequestId)
        {
            Definition = definition;
            CreatedAt = createdAt ?? DateTime.MinValue;
        }

        public override void UpdateDatabaseRecord(DatabaseRecord record, long etag)
        {
            try
            {
                var setting = PutIndexCommand.GetGlobalRollingSetting(record);
                record.AddIndex(Definition, CreatedAt, etag, setting);
            }
            catch (Exception e)
            {
                throw new RachisApplyException("Failed to update auto-index", e);
            }
            
        }

        public override void FillJson(DynamicJsonValue json)
        {
            json[nameof(Definition)] = TypeConverter.ToBlittableSupportedType(Definition);
            json[nameof(CreatedAt)] = TypeConverter.ToBlittableSupportedType(CreatedAt);
        }

        public static PutAutoIndexCommand Create(AutoIndexDefinitionBase definition, string databaseName, string raftRequestId)
        {
            var indexType = GetAutoIndexType(definition);

            return new PutAutoIndexCommand(GetAutoIndexDefinition(definition, indexType), databaseName, raftRequestId, DateTime.UtcNow);
        }

        public static IndexType GetAutoIndexType(AutoIndexDefinitionBase definition)
        {
            var indexType = IndexType.None;
            if (definition is AutoMapIndexDefinition)
                indexType = IndexType.AutoMap;

            if (definition is AutoMapReduceIndexDefinition)
                indexType = IndexType.AutoMapReduce;

            if (indexType == IndexType.None)
                throw new RachisApplyException($"Invalid definition type: {definition.GetType()}");

            return indexType;
        }

        public static AutoIndexDefinition GetAutoIndexDefinition(AutoIndexDefinitionBase definition, IndexType indexType)
        {
            Debug.Assert(indexType == IndexType.AutoMap || indexType == IndexType.AutoMapReduce);

            return new AutoIndexDefinition
            {
                Collection = definition.Collections.First(),
                MapFields = CreateFields(definition.MapFields.ToDictionary(x => x.Key, x => x.Value.As<AutoIndexField>())),
                GroupByFields = indexType == IndexType.AutoMap ? null : CreateFields(((AutoMapReduceIndexDefinition)definition).GroupByFields),
                Priority = definition.Priority,
                Name = definition.Name,
                Type = indexType,
                Rolling = definition.Rolling
            };
        }

        private static Dictionary<string, AutoIndexDefinition.AutoIndexFieldOptions> CreateFields(Dictionary<string, AutoIndexField> fields)
        {
            if (fields == null)
                return null;

            var result = new Dictionary<string, AutoIndexDefinition.AutoIndexFieldOptions>();

            foreach (var kvp in fields)
            {
                var autoField = kvp.Value;

                result[kvp.Key] = new AutoIndexDefinition.AutoIndexFieldOptions
                {
                    Storage = autoField.Storage,
                    Indexing = autoField.Indexing,
                    Aggregation = autoField.Aggregation,
                    Spatial = autoField.Spatial,
                    IsNameQuoted = autoField.HasQuotedName,
                    GroupByArrayBehavior = autoField.GroupByArrayBehavior,
                    Suggestions = autoField.HasSuggestions
                };
            }

            return result;
        }
    }
}
