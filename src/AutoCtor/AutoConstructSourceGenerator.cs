﻿using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AutoCtor;

[Generator(LanguageNames.CSharp)]
public class AutoConstructSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider.CreateSyntaxProvider(IsCorrectAttribute, GetTypeFromAttribute)
            .Where(t => t != null)
            .Collect();

#pragma warning disable CS8622
        context.RegisterSourceOutput(types, GenerateSource);
#pragma warning restore CS8622
    }

    private static bool IsCorrectAttribute(SyntaxNode syntaxNode, CancellationToken cancellationToken)
    {
        if (syntaxNode is not AttributeSyntax attribute)
            return false;

        var name = attribute.Name switch
        {
            SimpleNameSyntax ins => ins.Identifier.Text,
            QualifiedNameSyntax qns => qns.Right.Identifier.Text,
            _ => null
        };

        return IsCorrectAttributeName(name);
    }

    private static bool IsCorrectAttributeName(string? name) => name == "AutoConstruct" || name == "AutoConstructAttribute";

    private static ITypeSymbol? GetTypeFromAttribute(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var attributeSyntax = (AttributeSyntax)context.Node;

        // "attribute.Parent" is "AttributeListSyntax"
        // "attribute.Parent.Parent" is a C# fragment the attributes are applied to
        TypeDeclarationSyntax? typeNode = attributeSyntax.Parent?.Parent switch
        {
            ClassDeclarationSyntax classDeclarationSyntax => classDeclarationSyntax,
            RecordDeclarationSyntax recordDeclarationSyntax => recordDeclarationSyntax,
            StructDeclarationSyntax structDeclarationSyntax => structDeclarationSyntax,
            _ => null
        };

        if (typeNode == null)
            return null;

        if (context.SemanticModel.GetDeclaredSymbol(typeNode) is not ITypeSymbol type)
            return null;

        return type;
    }

    private static void GenerateSource(SourceProductionContext context, ImmutableArray<ITypeSymbol> types)
    {
        if (types.IsDefaultOrEmpty)
            return;

        var ctorMaps = new Dictionary<ITypeSymbol, IEnumerable<string>>(SymbolEqualityComparer.Default);

        var baseTypes = types.Where(t => t.BaseType == null || !types.Contains(t.BaseType));
        var extendedTypes = types.Except(baseTypes);

        foreach (var type in baseTypes.Concat(extendedTypes))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            IEnumerable<string>? baseParameters = default;

            if (type.BaseType != null)
                ctorMaps.TryGetValue(type.BaseType, out baseParameters);

            (var source, var parameters) = GenerateSource(type, baseParameters);

            ctorMaps.Add(type, parameters);

            var hintSymbolDisplayFormat = new SymbolDisplayFormat(
                globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                miscellaneousOptions:
                    SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                    SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

            var hintName = type.ToDisplayString(hintSymbolDisplayFormat)
                .Replace('<', '[')
                .Replace('>', ']');

            context.AddSource($"{hintName}.g.cs", source);
        }
    }

    private static bool HasFieldInitialiser(IFieldSymbol symbol)
    {
        return symbol.DeclaringSyntaxReferences.Select(x => x.GetSyntax()).OfType<VariableDeclaratorSyntax>().Any(x => x.Initializer != null);
    }

    private static (SourceText, IEnumerable<string>) GenerateSource(ITypeSymbol type, IEnumerable<string>? baseParameters = default)
    {
        var ns = type.ContainingNamespace.IsGlobalNamespace
                ? null
                : type.ContainingNamespace.ToString();

        var fields = type.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.IsReadOnly && !f.IsStatic && f.CanBeReferencedByName && !HasFieldInitialiser(f));

        var parameters = fields
            .Select(f => $"{f.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {CreateFriendlyName(f.Name)}");

        var baseCtorParameters = Enumerable.Empty<string>();
        var baseCtorArgs = Enumerable.Empty<string>();

        if (type.BaseType != null)
        {
            var constructor = type.BaseType.Constructors.OnlyOrDefault(c => !c.IsStatic && c.Parameters.Any());
            if (constructor != null)
            {
                baseCtorParameters = constructor.Parameters
                    .Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {CreateFriendlyName(p.Name)}");
                baseCtorArgs = constructor.Parameters.Select(p => CreateFriendlyName(p.Name));
                parameters = baseCtorParameters.Concat(parameters);
            }
            else if (baseParameters != null)
            {
                baseCtorParameters = baseParameters.ToArray();
                baseCtorArgs = baseParameters.Select(p => p.Split(' ')[1]).ToArray();
                parameters = baseCtorParameters.Concat(parameters);
            }
        }

        var typeKeyword = type.IsRecord ? "record" : "class";

        var source = new CodeBuilder();
        source.AppendLine($"//------------------------------------------------------------------------------");
        source.AppendLine($"// <auto-generated>");
        source.AppendLine($"//     This code was generated by https://github.com/distantcam/AutoCtor");
        source.AppendLine($"//");
        source.AppendLine($"//     Changes to this file may cause incorrect behavior and will be lost if");
        source.AppendLine($"//     the code is regenerated.");
        source.AppendLine($"// </auto-generated>");
        source.AppendLine($"//------------------------------------------------------------------------------");
        source.AppendLine($"");

        if (ns is not null)
        {
            source.AppendLine($"namespace {ns}");
            source.StartBlock();
        }

        var typeStack = new Stack<string>();

        var containingType = type.ContainingType;
        while (containingType is not null)
        {
            var contTypeKeyword = containingType.IsRecord ? "record" : "class";
            var typeName = containingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            typeStack.Push(contTypeKeyword + " " + typeName);
            containingType = containingType.ContainingType;
        }

        var nestedCount = typeStack.Count;

        while (typeStack.Count > 0)
        {
            source.AppendLine($"partial {typeStack.Pop()}");
            source.StartBlock();
        }

        source.AppendLine($"partial {typeKeyword} {type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
        source.StartBlock();

        if (baseCtorParameters.Any())
            source.AppendLine($"public {type.Name}({parameters.AsParams()}) : base({baseCtorArgs.AsParams()})");
        else
            source.AppendLine($"public {type.Name}({parameters.AsParams()})");
        source.StartBlock();

        foreach (var item in fields)
        {
            source.AppendLine($"this.{item.Name} = {CreateFriendlyName(item.Name)};");
        }

        source.AppendLine("Initialize();");

        source.EndBlock();

        source.AppendLine("partial void Initialize();");

        source.EndBlock();

        for (var i = 0; i < nestedCount; i++)
        {
            source.EndBlock();
        }

        if (ns is not null)
        {
            source.EndBlock();
        }

        return (source, parameters);
    }

    private static string CreateFriendlyName(string name)
    {
        if (name.Length > 1 && name[0] == '_')
        {
            // Chop off the underscore at the start
            return name.Substring(1);
        }

        return name;
    }
}
