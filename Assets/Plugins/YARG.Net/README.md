# YARG.Net Unity Drop

Local development temporarily references the networking library by copying the Release-built DLLs straight into `Assets/Plugins/YARG.Net/`. This keeps the iteration loop fast while we are still touching the runtime and Unity client in tandem.

## Refresh Steps

1. Build the library in Release mode from the `YARG.Networking` repo:
   ```powershell
   cd ..\YARG.Networking
   dotnet build src/YARG.Net/YARG.Net.csproj -c Release
   ```
2. Copy the outputs into this folder:
   ```powershell
   $src = "..\YARG.Networking\src\YARG.Net\bin\Release\netstandard2.1"
   $dst = "..\YARG\Assets\Plugins\YARG.Net"
   Copy-Item -Path (Join-Path $src "YARG.Net.dll") -Destination $dst -Force
   Copy-Item -Path (Join-Path $src "YARG.Net.xml") -Destination $dst -Force
   Copy-Item -Path (Join-Path $src "LiteNetLib.dll") -Destination $dst -Force
   Copy-Item -Path (Join-Path $src "Newtonsoft.Json.dll") -Destination $dst -Force
   Copy-Item -Path (Join-Path $src "Microsoft.Bcl.AsyncInterfaces.dll") -Destination $dst -Force
   Copy-Item -Path (Join-Path $src "System.Buffers.dll") -Destination $dst -Force
   Copy-Item -Path (Join-Path $src "System.Memory.dll") -Destination $dst -Force
   Copy-Item -Path (Join-Path $src "System.Numerics.Vectors.dll") -Destination $dst -Force
   Copy-Item -Path (Join-Path $src "System.Runtime.CompilerServices.Unsafe.dll") -Destination $dst -Force
   Copy-Item -Path (Join-Path $src "System.Text.Encodings.Web.dll") -Destination $dst -Force
   Copy-Item -Path (Join-Path $src "System.Text.Json.dll") -Destination $dst -Force
   Copy-Item -Path (Join-Path $src "System.Threading.Tasks.Extensions.dll") -Destination $dst -Force
   ```

Unity will generate the `.meta` files the next time the editor refreshes. Once the pipeline stabilizes we can swap this folder out for a proper package reference.
