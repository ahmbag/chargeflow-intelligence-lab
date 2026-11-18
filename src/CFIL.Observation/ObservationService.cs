namespace CFIL.Observation;

public sealed class ObservationPendingException : Exception
{
    public ObservationPendingException():base("The producer has not published a complete initial view yet."){}
}

public sealed class ObservationService(RunCatalog catalog)
{
    private readonly Dictionary<string,Task<RunProjection>> projections=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim catalogGate=new(1,1);
    public RunCatalog Catalog { get; }=catalog;
    public async Task<RunProjection> GetAsync(string key)
    {
        Task<RunProjection> task;
        await catalogGate.WaitAsync();
        try
        {
            if(!projections.TryGetValue(key,out task!))
            {
                var source=Catalog.ResolveKey(key);
                var overview=RunCatalog.Overview(source);
                if(overview.Integrity=="CHECKSUM_MISMATCH") throw new InvalidDataException("Accepted source changed.");
                // SUMO can create its network file before finishing its contents.
                // The running manifest is the producer's initialization boundary;
                // waiting here must not poison the shared cache with a failed task.
                if(overview.Status=="initializing")throw new ObservationPendingException();
                if(projections.Count>=4)
                {
                    var oldest=projections.Where(p=>p.Value.IsCompletedSuccessfully).OrderBy(p=>p.Value.Result.LastUsed).FirstOrDefault();
                    if(oldest.Key is null)throw new InvalidOperationException("Observation capacity is busy; retry after indexing.");
                    projections.Remove(oldest.Key);
                }
                // Browser circuits share this task. Concurrent first opens do not each
                // scan hundreds of megabytes or keep an independent event backlog.
                task=Task.Run(async()=>{var p=new RunProjection(source);await p.InitializeAsync();return p;});
                projections.Add(key,task);
            }
        }
        finally {catalogGate.Release();}
        var projection=await task;projection.LastUsed=DateTimeOffset.UtcNow;return projection;
    }
    public async Task<T> ReadAsync<T>(string key,Func<RunProjection,T> query)
    {
        var projection=await GetAsync(key);await projection.Gate.WaitAsync();
        try {projection.RefreshBeforeRead();return query(projection);}finally{projection.Gate.Release();}
    }
    public async Task RefreshActiveAsync()
    {
        RunProjection[] active;await catalogGate.WaitAsync();
        try {active=projections.Values.Where(t=>t.IsCompletedSuccessfully).Select(t=>t.Result).Where(p=>DateTimeOffset.UtcNow-p.LastUsed<TimeSpan.FromMinutes(2)).ToArray();}
        finally {catalogGate.Release();}
        foreach(var p in active)await p.RefreshAsync();
    }
    public async Task<ProjectionStatistics> StatisticsAsync()
    {
        await catalogGate.WaitAsync();try
        {
            var stats=projections.Values.Where(t=>t.IsCompletedSuccessfully).Select(t=>t.Result.Statistics).ToArray();
            return new(projections.Count,stats.Sum(s=>s.SourceBytesRead),stats.Sum(s=>s.SourcePasses),stats.Sum(s=>s.CriticalEvents),stats.Sum(s=>s.SourceRecords),stats.Sum(s=>s.FramePointers),
                FrameLoads:stats.Sum(s=>s.FrameLoads),SnapshotFrames:stats.Sum(s=>s.SnapshotFrames),TelemetryFrames:stats.Sum(s=>s.TelemetryFrames));
        }finally {catalogGate.Release();}
    }
    public async Task<PairComparison> CompareAsync(string left,string right,long? cursor=null,bool retrospect=false)
    {
        if(left==right)throw new ArgumentException("Select two different runs.");
        var l=await GetAsync(left);var r=await GetAsync(right);
        // Global key ordering prevents two viewers comparing A/B and B/A from
        // deadlocking while taking a consistent pair of immutable read snapshots.
        var first=string.CompareOrdinal(left,right)<0?l:r;var second=ReferenceEquals(first,l)?r:l;
        await first.Gate.WaitAsync();await second.Gate.WaitAsync();
        try{return ComparisonReader.Compare(l,r,cursor,retrospect);}finally{second.Gate.Release();first.Gate.Release();}
    }
}
