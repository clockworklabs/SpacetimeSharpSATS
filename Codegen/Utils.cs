namespace SpacetimeDB;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System;

static class Utils
{
    internal static string SymbolToName(ISymbol symbol)
    {
        return symbol.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat
                .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType)
                .WithGenericsOptions(SymbolDisplayGenericsOptions.None)
                .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
        );
    }

    internal static void RegisterSourceOutputs(
        this IncrementalValuesProvider<KeyValuePair<string, string>> methods,
        IncrementalGeneratorInitializationContext context
    )
    {
        context.RegisterSourceOutput(
            methods,
            (context, method) =>
            {
                context.AddSource(
                    $"{string.Join("_", method.Key.Split(System.IO.Path.GetInvalidFileNameChars()))}.cs",
                    method.Value
                );
            }
        );
    }

    public static string GetTypeInfo(ITypeSymbol type)
    {
        if (type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            return $"SpacetimeDB.SATS.SumType.MakeOption({GetTypeInfo(type.WithNullableAnnotation(NullableAnnotation.None))})";
        }
        return type switch
        {
            ITypeParameterSymbol typeParameter => $"{typeParameter.Name}TypeInfo",
            INamedTypeSymbol namedType
                => type.SpecialType switch
                {
                    SpecialType.System_Boolean => "SpacetimeDB.SATS.BuiltinType.BoolTypeInfo",
                    SpecialType.System_SByte => "SpacetimeDB.SATS.BuiltinType.I8TypeInfo",
                    SpecialType.System_Byte => "SpacetimeDB.SATS.BuiltinType.U8TypeInfo",
                    SpecialType.System_Int16 => "SpacetimeDB.SATS.BuiltinType.I16TypeInfo",
                    SpecialType.System_UInt16 => "SpacetimeDB.SATS.BuiltinType.U16TypeInfo",
                    SpecialType.System_Int32 => "SpacetimeDB.SATS.BuiltinType.I32TypeInfo",
                    SpecialType.System_UInt32 => "SpacetimeDB.SATS.BuiltinType.U32TypeInfo",
                    SpecialType.System_Int64 => "SpacetimeDB.SATS.BuiltinType.I64TypeInfo",
                    SpecialType.System_UInt64 => "SpacetimeDB.SATS.BuiltinType.U64TypeInfo",
                    // TODO: IU128
                    SpecialType.System_Single
                        => "SpacetimeDB.SATS.BuiltinType.F32TypeInfo",
                    SpecialType.System_Double => "SpacetimeDB.SATS.BuiltinType.F64TypeInfo",
                    SpecialType.System_String => "SpacetimeDB.SATS.BuiltinType.StringTypeInfo",
                    SpecialType.None when type.ToString() == "System.Int128"
                        => "SpacetimeDB.SATS.BuiltinType.I128TypeInfo",
                    SpecialType.None when type.ToString() == "System.UInt128"
                        => "SpacetimeDB.SATS.BuiltinType.U128TypeInfo",
                    SpecialType.None
                        => $"{type.OriginalDefinition.ToString() switch
                    {
                        "System.Collections.Generic.List<T>" => "SpacetimeDB.SATS.BuiltinType.MakeList",
                        "System.Collections.Generic.Dictionary<TKey, TValue>" => "SpacetimeDB.SATS.BuiltinType.MakeMap",
                        var name when name.StartsWith("System.") => throw new InvalidOperationException(
                            $"Unsupported system type {name}"
                        ),
                        _ => $"{type}.GetSatsTypeInfo",
                    }}({string.Join(", ", namedType.TypeArguments.Select(GetTypeInfo))})",
                    _
                        => throw new InvalidOperationException(
                            $"Unsupported special type {type.SpecialType} ({type})"
                        )
                },
            IArrayTypeSymbol arrayType
                => arrayType.ElementType is INamedTypeSymbol namedType && namedType.SpecialType == SpecialType.System_Byte
                   ? "SpacetimeDB.SATS.BuiltinType.BytesTypeInfo"
                   : $"SpacetimeDB.SATS.BuiltinType.MakeArray({GetTypeInfo(arrayType.ElementType)})",
            _ => throw new InvalidOperationException($"Unsupported type {type}")
        };
    }

    // Borrowed & modified code for generating in-place extensions for partial structs/classes/etc. Source:
    // https://andrewlock.net/creating-a-source-generator-part-5-finding-a-type-declarations-namespace-and-type-hierarchy/

    public class Scope
    {
        private string nameSpace;
        private ParentClass? parentClasses;

        public Scope(TypeDeclarationSyntax type)
        {
            nameSpace = GetNamespace(type);
            parentClasses = GetParentClasses(type);
        }

        // determine the namespace the class/enum/struct is declared in, if any
        static string GetNamespace(BaseTypeDeclarationSyntax syntax)
        {
            // If we don't have a namespace at all we'll return an empty string
            // This accounts for the "default namespace" case
            string nameSpace = string.Empty;

            // Get the containing syntax node for the type declaration
            // (could be a nested type, for example)
            SyntaxNode? potentialNamespaceParent = syntax.Parent;

            // Keep moving "out" of nested classes etc until we get to a namespace
            // or until we run out of parents
            while (
                potentialNamespaceParent != null
                && potentialNamespaceParent is not NamespaceDeclarationSyntax
                && potentialNamespaceParent is not FileScopedNamespaceDeclarationSyntax
            )
            {
                potentialNamespaceParent = potentialNamespaceParent.Parent;
            }

            // Build up the final namespace by looping until we no longer have a namespace declaration
            if (potentialNamespaceParent is BaseNamespaceDeclarationSyntax namespaceParent)
            {
                // We have a namespace. Use that as the type
                nameSpace = namespaceParent.Name.ToString();

                // Keep moving "out" of the namespace declarations until we
                // run out of nested namespace declarations
                while (true)
                {
                    if (namespaceParent.Parent is not NamespaceDeclarationSyntax parent)
                    {
                        break;
                    }

                    // Add the outer namespace as a prefix to the final namespace
                    nameSpace = $"{namespaceParent.Name}.{nameSpace}";
                    namespaceParent = parent;
                }
            }

            // return the final namespace
            return nameSpace;
        }

        public class ParentClass
        {
            public ParentClass(string keyword, string name, string constraints, ParentClass? child)
            {
                Keyword = keyword;
                Name = name;
                Constraints = constraints;
                Child = child;
            }

            public ParentClass? Child { get; }
            public string Keyword { get; }
            public string Name { get; }
            public string Constraints { get; }
        }

        static ParentClass? GetParentClasses(TypeDeclarationSyntax typeSyntax)
        {
            // Try and get the parent syntax. If it isn't a type like class/struct, this will be null
            TypeDeclarationSyntax? parentSyntax = typeSyntax;
            ParentClass? parentClassInfo = null;

            // Keep looping while we're in a supported nested type
            while (parentSyntax != null && IsAllowedKind(parentSyntax.Kind()))
            {
                // Record the parent type keyword (class/struct etc), name, and constraints
                parentClassInfo = new ParentClass(
                    keyword: parentSyntax.Keyword.ValueText,
                    name: parentSyntax.Identifier.ToString() + parentSyntax.TypeParameterList,
                    constraints: parentSyntax.ConstraintClauses.ToString(),
                    child: parentClassInfo
                ); // set the child link (null initially)

                // Move to the next outer type
                parentSyntax = (parentSyntax.Parent as TypeDeclarationSyntax);
            }

            // return a link to the outermost parent type
            return parentClassInfo;
        }

        // We can only be nested in class/struct/record
        static bool IsAllowedKind(SyntaxKind kind) =>
            kind == SyntaxKind.ClassDeclaration
            || kind == SyntaxKind.StructDeclaration
            || kind == SyntaxKind.RecordDeclaration;

        public string GenerateExtensions(string contents)
        {
            var sb = new StringBuilder();

            // If we don't have a namespace, generate the code in the "default"
            // namespace, either global:: or a different <RootNamespace>
            var hasNamespace = !string.IsNullOrEmpty(nameSpace);
            if (hasNamespace)
            {
                // We could use a file-scoped namespace here which would be a little impler,
                // but that requires C# 10, which might not be available.
                // Depends what you want to support!
                sb.Append("namespace ")
                    .Append(nameSpace)
                    .AppendLine(
                        @"
        {"
                    );
            }

            // Loop through the full parent type hiearchy, starting with the outermost
            var parentsCount = 0;
            while (parentClasses is not null)
            {
                sb.Append("    partial ")
                    .Append(parentClasses.Keyword) // e.g. class/struct/record
                    .Append(' ')
                    .Append(parentClasses.Name) // e.g. Outer/Generic<T>
                    .Append(' ')
                    .Append(parentClasses.Constraints) // e.g. where T: new()
                    .AppendLine(
                        @"
            {"
                    );
                parentsCount++; // keep track of how many layers deep we are
                parentClasses = parentClasses.Child; // repeat with the next child
            }

            // Write the actual target generation code here. Not shown for brevity
            sb.AppendLine(contents);

            // We need to "close" each of the parent types, so write
            // the required number of '}'
            for (int i = 0; i < parentsCount; i++)
            {
                sb.AppendLine(@"    }");
            }

            // Close the namespace, if we had one
            if (hasNamespace)
            {
                sb.Append('}').AppendLine();
            }

            return sb.ToString();
        }
    }
}