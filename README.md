# ILRecover
Decompile .NET assemblies back to C# source. It uses the PDB to properly map the IL

> [!NOTE]  
> This not produce always guarantee compilable decompile you might need to do some fixes and cleanup.

Why do this why not just directly `ICSharpCode.Decompiler`?
> Some devs don't follow conventions [file-scoped namespaces](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/namespaces) get ignored, class names don't match their filenames, and a single file sometimes holds multiple or partial classes. Decompilers like ILSpy don't account for any of that, so the output ends up in the wrong place or structured nothing like the original source. ILRecover aims to decompile closely to it's original structure with the help of PDB.

## Requirements
The compiled .NET assembly (DLL/EXE) and the PDB, both Portable PDB and 
Windows/Microsoft C PDB (with some limitations and inaccuracy) should work. 
If you don't have the PDB we recommend just using [ILSpy](https://github.com/icsharpcode/ilspy).

## Usage

```bash
# Show help
ILRecover.exe --help

ILRecover.exe -i "DLL_Folder" -o "Output_Folder" -cs 14 -dn "net10.0" -dp "Dependencies_Folder"
```

## Build

1. Install [.NET SDK](https://dotnet.microsoft.com/en-us/download)
2. Clone this repository

```sh
git clone --recursive https://github.com/KaniArchive/ILRecover
cd ILRecover
```

3. Build using `dotnet`

```sh
dotnet build
```

## License

`ILRecover` is under **MIT**. See [LICENSE](LICENSE) for copyright and license details.
