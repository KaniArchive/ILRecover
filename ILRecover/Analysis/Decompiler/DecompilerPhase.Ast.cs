using System.Reflection.Metadata;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.TypeSystem;
using ZLinq;

namespace ILRecover.Analysis.Decompiler;

public partial class DecompilerPhase
{
    private static SyntaxTree? FilterTypeTree(
        SyntaxTree tree,
        HashSet<EntityHandle> selectedTokens,
        bool forcePartial,
        bool preserveSharedTypeMembers)
    {
        var clone = (SyntaxTree)tree.Clone();

        foreach (var member in clone.Members.ToList().AsValueEnumerable()
                     .Where(member => !PruneNode(member, selectedTokens, preserveSharedTypeMembers)))
            member.Remove();

        if (clone.Members.Count == 0)
            return null;

        if (!forcePartial) return clone;
        var topLevelType = FindFirstTopLevelType(clone);
        topLevelType?.Modifiers |= Modifiers.Partial;

        return clone;
    }

    private static bool PruneNode(AstNode node, HashSet<EntityHandle> selectedTokens, bool preserveSharedTypeMembers)
    {
        switch (node)
        {
            case NamespaceDeclaration ns:
                foreach (var member in ns.Members.ToList().AsValueEnumerable()
                             .Where(member => !PruneNode(member, selectedTokens, preserveSharedTypeMembers)))
                    member.Remove();

                return ns.Members.Count > 0;

            case TypeDeclaration typeDecl:
                foreach (var member in typeDecl.Members.ToList().AsValueEnumerable()
                             .Where(member => !PruneNode(member, selectedTokens, preserveSharedTypeMembers)))
                    member.Remove();

                return typeDecl.Members.Count > 0
                       || HasSelectedDeclaration(typeDecl, selectedTokens)
                       || preserveSharedTypeMembers;

            case EntityDeclaration entity:
                return HasSelectedDeclaration(entity, selectedTokens)
                       || preserveSharedTypeMembers && IsSharedTypeLevelMember(entity);

            default:
                return false;
        }
    }

    private static bool IsSharedTypeLevelMember(EntityDeclaration entity) =>
        entity switch
        {
            FieldDeclaration => true,
            PropertyDeclaration => true,
            EventDeclaration => true,
            IndexerDeclaration => true,
            OperatorDeclaration => true,
            ConstructorDeclaration => true,
            DestructorDeclaration => true,
            TypeDeclaration => true,
            DelegateDeclaration => true,
            EnumMemberDeclaration => true,
            _ => false
        };

    private static bool HasSelectedDeclaration(AstNode node, HashSet<EntityHandle> selectedTokens)
    {
        if (node.GetSymbol() is IEntity { MetadataToken.IsNil: false } entity
            && selectedTokens.Contains(entity.MetadataToken))
            return true;

        foreach (var child in node.Children)
            if (child is EntityDeclaration or NamespaceDeclaration)
                if (HasSelectedDeclaration(child, selectedTokens))
                    return true;

        return false;
    }

    private static TypeDeclaration? FindFirstTopLevelType(SyntaxTree tree)
    {
        foreach (var member in tree.Members)
            switch (member)
            {
                case TypeDeclaration typeDecl:
                    return typeDecl;
                case NamespaceDeclaration ns:
                {
                    var nested = ns.Members.OfType<TypeDeclaration>().FirstOrDefault();
                    if (nested is not null)
                        return nested;
                    break;
                }
            }

        return null;
    }

    private static SyntaxTree MergeSyntaxTrees(SyntaxTree target, SyntaxTree source)
    {
        foreach (var usingDecl in source.Children.OfType<UsingDeclaration>())
            if (target.Children.OfType<UsingDeclaration>().AsValueEnumerable()
                .All(u => u.ToString() != usingDecl.ToString()))
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
                .FirstOrDefault(n => n.Name == sourceNamespace.Name);

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
                .FirstOrDefault(t => TypeDeclarationsMatch(t, sourceType));

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
