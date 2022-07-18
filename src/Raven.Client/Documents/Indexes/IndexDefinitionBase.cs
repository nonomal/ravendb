﻿using Sparrow.Json;

namespace Raven.Client.Documents.Indexes
{
    public abstract class IndexDefinitionBase
    {
        protected IndexDefinitionBase()
        {
            ClusterState = new ClusterState();
        }

        /// <summary>
        /// This is the means by which the outside world refers to this index definition
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Priority of an index
        /// </summary>
        public IndexPriority? Priority { get; set; }

        /// <summary>
        /// Index state
        /// </summary>
        public IndexState? State { get; set; }

        [ForceJsonSerialization]
        internal ClusterState ClusterState;
    }
}
