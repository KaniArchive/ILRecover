using ICSharpCode.Decompiler.CSharp.Syntax;
using ZLinq;

namespace ILRecover.Analysis.Decompiler;

public partial class DecompilerPhase
{
    private static SyntaxTree MergeSyntaxTrees(SyntaxTree target, SyntaxTree source)
    {
        foreach (var usingDecl in source.Children.OfType<UsingDeclaration>())
            if (target.Children.OfType<UsingDeclaration>().AsValueEnumerable()
                .All(existingUsing => existingUsing.ToString() != usingDecl.ToString()))
                target.AddChild(usingDecl.Clone(), SyntaxTree.MemberRole);

        foreach (var member in source.Members)
            MergeTopLevelMember(target, member);

        return target;
    }

    private static void MergeTopLevelMember(SyntaxTree target, AstNode member)
    {
        if (member is NamespaceDeclaration sourceNamespace)
        {
            var targetNamespace = target.Members
                .OfType<NamespaceDeclaration>()
                .AsValueEnumerable()
                .FirstOrDefault(ns => ns.Name == sourceNamespace.Name);

            if (targetNamespace is null)
            {
                target.AddChild(sourceNamespace.Clone(), SyntaxTree.MemberRole);
                return;
            }

            foreach (var nestedMember in sourceNamespace.Members)
                MergeNamespaceMember(targetNamespace, nestedMember);

            return;
        }

        target.AddChild(member.Clone(), SyntaxTree.MemberRole);
    }

    private static void MergeNamespaceMember(NamespaceDeclaration targetNamespace, AstNode member)
    {
        if (member is TypeDeclaration sourceType)
        {
            var targetType = targetNamespace.Members
                .OfType<TypeDeclaration>()
                .AsValueEnumerable()
                .FirstOrDefault(type => TypeDeclarationsMatch(type, sourceType));

            if (targetType is null)
            {
                targetNamespace.AddChild(sourceType.Clone(), NamespaceDeclaration.MemberRole);
                return;
            }

            foreach (var nestedMember in sourceType.Members)
                targetType.Members.Add((EntityDeclaration)nestedMember.Clone());

            return;
        }

        targetNamespace.AddChild(member.Clone(), NamespaceDeclaration.MemberRole);
    }

    private static bool TypeDeclarationsMatch(TypeDeclaration left, TypeDeclaration right) =>
        left.Name == right.Name
        && left.ClassType == right.ClassType
        && left.TypeParameters.Count == right.TypeParameters.Count;
}