using System;
using System.Collections.Generic;
using System.Text;

namespace SLVZ.Db.Light;

public abstract class LightContext
{
    public abstract void Configuration();

    protected LightContext()
    {
        Configuration();
    }

    public virtual DataSet<TModel> DataSet<TModel>(string path) where TModel : class
    => new DataSet<TModel>(path);

    /// <summary>
    /// durable = true flushes every write to the storage chip before the call returns (survives power loss, slightly slower).
    /// Use it for data that must never be lost; leave it off for cache data.
    /// </summary>
    public virtual DataSet<TModel> DataSet<TModel>(string path, bool durable) where TModel : class
    => new DataSet<TModel>(path, durable);

}

