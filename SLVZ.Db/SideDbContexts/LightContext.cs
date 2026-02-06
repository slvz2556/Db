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

}
