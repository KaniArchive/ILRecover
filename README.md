# ILRecover
Decompile .NET assemblies back to C# source. It uses the PDB to properly map the IL

> [!WARNING]  
> This project is on WIP state the decompiled may or not compile and some IL probably wouldn't decompile.

Why do this why not just directly `ICSharpCode.Decompiler`?
> Some devs don't follow conventions [file-scoped namespaces](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/namespaces) get ignored, class names don't match their filenames, and a single file sometimes holds multiple or partial classes. Decompilers like ILSpy don't account for any of that, so the output ends up in the wrong place or structured nothing like the original source.

## Usage

```bash
# Show help
ILRecover.exe --help

ILRecover.exe -i "DLL_Folder" -o "Ouput_Folder" -ec ".editorconfig" -cs 14 -dp "Dependencies_Folder"
ILRecover.exe -i "..\_no\Source" -o "..\_no\cs10" -cs 10 -sl "cs10" -dp "..\_no\Dependencies"
```

When the input folder contains multiple `*.dll` files with matching `*.pdb` files, ILRecover writes one project folder per assembly and creates a `.slnx` file in the output folder. If `-sl` is omitted, the solution name defaults to the output folder name.

## Build

1. Install [.NET SDK](https://dotnet.microsoft.com/en-us/download)
2. Clone this repository

```sh
git clone https://github.com/KaniArchive/ILRecover
cd ILRecover
```

3. Build using `dotnet`

```sh
dotnet build
```

## License

`ILRecover` is under **MIT**. See [LICENSE](LICENSE) for copyright and license details.
