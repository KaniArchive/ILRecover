using ICSharpCode.Decompiler.CSharp.Syntax;
using ILRecover.Models;
using ZLinq;
using CSharpAttribute = ICSharpCode.Decompiler.CSharp.Syntax.Attribute;

namespace ILRecover.Analysis.SourceGen;

internal static partial class SourceGenNormalizer
{
    private static bool ShouldMergeMemoryPackGeneratedCompanion(SourceFileMap file) =>
        !SourceGenPaths.IsMemoryPackGeneratorPath(file.OriginalPath);

    private static void NormalizeMemoryPack(SyntaxTree tree)
    {
        foreach (var type in tree.Descendants.OfType<TypeDeclaration>().ToList())
        {
            if (!IsMemoryPackType(type))
                continue;

            type.Modifiers |= Modifiers.Partial;
            RemoveDefaultMemoryPackGenerateTypeArguments(type);
            RemoveBaseTypes(type, "IMemoryPackable", "IMemoryPackFormatterRegister");

            foreach (var nestedType in type.Members.AsValueEnumerable().OfType<TypeDeclaration>().ToList().Where(nestedType =>
                         nestedType.BaseTypes.Any(baseType => AstTypeContains(baseType, "MemoryPackFormatter"))))
                nestedType.Remove();
        }
    }

    private static bool IsMemoryPackType(TypeDeclaration type) =>
        HasAttribute(type, "MemoryPackable")
        || type.BaseTypes.Any(baseType =>
            AstTypeContains(baseType, "IMemoryPackable")
            || AstTypeContains(baseType, "IMemoryPackFormatterRegister"));

    private static void RemoveDefaultMemoryPackGenerateTypeArguments(TypeDeclaration type)
    {
        foreach (var attribute in type.Attributes
                     .AsValueEnumerable()
                     .SelectMany(section => section.Attributes)
                     .Where(attribute => AstTypeContains(attribute.Type, "MemoryPackable")))
            RemoveDefaultMemoryPackGenerateTypeArgument(attribute);
    }

    private static void RemoveDefaultMemoryPackGenerateTypeArgument(CSharpAttribute attribute)
    {
        var arguments = attribute.Arguments.ToList();
        if (arguments.Count != 1 || !IsDefaultMemoryPackGenerateTypeArgument(arguments[0]))
            return;

        arguments[0].Remove();
        attribute.HasArgumentList = false;
    }

    private static bool IsDefaultMemoryPackGenerateTypeArgument(Expression argument) =>
        IsGenerateTypeObject(argument)
        || (argument is AssignmentExpression
            {
                Operator: AssignmentOperatorType.Assign, Left: var left, Right: var right
            }
            && IsMemoryPackGenerateTypeParameter(left)
            && IsGenerateTypeObject(right));

    private static bool IsMemoryPackGenerateTypeParameter(Expression expression) =>
        expression is IdentifierExpression { Identifier: "GenerateType" or "GenerateType_" or "generateType" };

    private static bool IsGenerateTypeObject(Expression expression) =>
        expression is MemberReferenceExpression { MemberName: "Object", Target: var target }
        && IsGenerateTypeReference(target);

    private static bool IsGenerateTypeReference(Expression expression) =>
        expression is IdentifierExpression { Identifier: "GenerateType" }
        || (expression is TypeReferenceExpression { Type: var type } && AstTypeContains(type, "GenerateType"))
        || expression is MemberReferenceExpression { MemberName: "GenerateType" };

    private static void RemoveBaseTypes(TypeDeclaration type, params string[] typeNames)
    {
        foreach (var baseType in type.BaseTypes.ToList()
                     .AsValueEnumerable()
                     .Where(baseType => typeNames.Any(typeName => AstTypeContains(baseType, typeName))))
            baseType.Remove();
    }

    private static bool HasAttribute(EntityDeclaration declaration, string name) =>
        declaration.Attributes
            .AsValueEnumerable()
            .SelectMany(section => section.Attributes)
            .Any(attribute => AstTypeContains(attribute.Type, name));

    private static bool AstTypeContains(AstType type, string name) =>
        type.ToString().Contains(name, StringComparison.Ordinal);
}