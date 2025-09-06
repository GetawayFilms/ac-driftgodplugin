using Autofac;
using AssettoServer.Server.Plugin;

namespace DriftGodPlugin;

public class DriftGodModule : AssettoServerModule<DriftGodConfiguration>
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<DriftGodPlugin>()
               .AsSelf()
               .As<IAssettoServerAutostart>()
               .SingleInstance();
               
        builder.RegisterType<DriftGodController>()
               .AsSelf()
               .SingleInstance();
    }
}