# RC2-DVM

Direct integration between a [RadioConsole2](https://github.com/W3AXL/RadioConsole2) instance and a [DVMProject FNE](https://github.com/DVMProject/dvmhost)!

## Building

**This program is currently Windows-only!**

Future work will allow for building on dotnet for linux as well, but currently `rc2-dvm` only compiles on a Windows x32 system because of interop requirements with native Windows x32 .dlls.

### External Dependecies

`RC2-DVM` requires a copy of the `libvocoder` software vocoder library from the [dvmvocoder](https://github.com/DVMProject/dvmvocoder). You can download the latest release yourself, or clone the repository and compile the .dll file using cmake directly.

### Building RC2-DVM

Use the following steps to build the project and create a single-file .exe:

```console
$ git clone --recurse-submodules https://github.com/W3AXL/rc2-dvm
$ cd rc2-dvm
$ dotnet restore
$ dotnet build
$ dotnet publish -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true "rc2-dvm/rc2-dvm.csproj"
```

Reference [`config.example.yml`](https://github.com/W3AXL/rc2-dvm/blob/main/rc2-dvm/config.example.yml) for information on configuring an `rc2-dvm` instance to communicate with your DVM FNE instance.
