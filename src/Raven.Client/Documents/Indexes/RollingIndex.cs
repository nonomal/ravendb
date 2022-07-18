﻿using System;
using System.Collections.Generic;
using Sparrow.Json.Parsing;

namespace Raven.Client.Documents.Indexes
{
    public class RollingIndex : IDynamicJson
    {
        public Dictionary<string, RollingIndexDeployment> ActiveDeployments = new Dictionary<string, RollingIndexDeployment>(StringComparer.OrdinalIgnoreCase);

        public long RaftCommandIndex;
        public DynamicJsonValue ToJson()
        {
            return new DynamicJsonValue
            {
                [nameof(ActiveDeployments)] = DynamicJsonValue.Convert(ActiveDeployments),
                [nameof(RaftCommandIndex)] = RaftCommandIndex
            };
        }
    }

    public class RollingIndexDeployment : IDynamicJson
    {
        public RollingIndexState State { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public DynamicJsonValue ToJson()
        {
            return new DynamicJsonValue
            {
                [nameof(State)] = State, 
                [nameof(CreatedAt)] = CreatedAt,
                [nameof(StartedAt)] = StartedAt,
                [nameof(FinishedAt)] = FinishedAt,
            };
        }
    }
    public enum RollingIndexState
    {
        Pending,
        Running,
        Done
    }
}
