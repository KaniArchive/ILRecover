using System.Reflection.Metadata;

namespace ILRecover.Models;

public record SourceFileTypeDeclarationEntry(
    string TypeFullName,
    TypeDefinitionHandle TypeHandle
);