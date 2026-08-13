# M5-08 Build Channels

Tessel v1 packaging keeps the product shell unpackaged and separates runtime identity by build channel.

- `Dev`: default for Debug builds. Display name `Tessel Dev`, single-instance key `Tessel.ProductShell.Dev`, local data root `%LocalAppData%\Tessel.Dev`.
- `PackagingTest`: explicit `-p:TesselChannel=PackagingTest`. Display name `Tessel Packaging Test`, single-instance key `Tessel.ProductShell.PackagingTest`, local data root `%LocalAppData%\Tessel.PackagingTest`.
- `Stable`: default for non-Debug builds. Display name `Tessel`, single-instance key `Tessel.ProductShell.Stable`, local data root `%LocalAppData%\Tessel`.

The channels share the same product code and safety semantics. Channel selection must not change Automation, Writer, Recovery, or Layout Editing authority.

Diagnostics remains an engineering workbench and keeps its existing data-path behavior. Only the WinUI product shell is routed through the channel-isolated product paths.

Planned M5-08 release path:

1. unpackaged self-contained `win-x64` publish;
2. `Tessel-x.y.z-Portable.zip` from the same publish payload;
3. Inno Setup packages that payload as `Tessel-x.y.z-Setup.exe`;
4. upgrade tests verify program files replace cleanly while channel-local user data remains intact.
