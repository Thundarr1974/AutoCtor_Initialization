﻿using System.Collections.Immutable;
using System.Text;
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

        context.RegisterSourceOutput(types, GenerateSource);
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

        return name == "AutoConstruct" || name == "AutoConstructAttribute";
    }

    private static ITypeSymbol GetTypeFromAttribute(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var attributeSyntax = (AttributeSyntax)context.Node;

        // "attribute.Parent" is "AttributeListSyntax"
        // "attribute.Parent.Parent" is a C# fragment the attributes are applied to
        if (attributeSyntax.Parent?.Parent is not ClassDeclarationSyntax classDeclaration)
            return null;

        if (context.SemanticModel.GetDeclaredSymbol(classDeclaration) is not ITypeSymbol type)
            return null;

        var hasCorrectAttribute = type.GetAttributes()
            .Any(a => (a.AttributeClass.Name == "AutoConstruct" || a.AttributeClass.Name == "AutoConstructAttribute"));

        return hasCorrectAttribute ? type : null;
    }

    private static void GenerateSource(SourceProductionContext context, ImmutableArray<ITypeSymbol> types)
    {
        if (types.IsDefaultOrEmpty)
            return;

        foreach (var type in types)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var source = GenerateSource(type);

            var nameParts = new List<string>();

            var containingType = type.ContainingType;
            while (containingType is not null)
            {
                nameParts.Add(containingType.ToDisplayString().Replace('<', '[').Replace('>', ']'));
                containingType = containingType.ContainingType;
            }

            if (!type.ContainingNamespace.IsGlobalNamespace)
            {
                nameParts.Add(type.ContainingNamespace.Name);
            }

            nameParts.Reverse();

            nameParts.Add(type.ToDisplayString().Replace('<', '[').Replace('>', ']'));
            nameParts.Add("g");
            nameParts.Add("cs");

            context.AddSource(string.Join(".", nameParts), SourceText.From(source, Encoding.UTF8));
        }
    }

    private static string GenerateSource(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace.IsGlobalNamespace
                ? null
                : type.ContainingNamespace.ToString();

        var fields = type.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.IsReadOnly);

        var parameters = fields.Select(f => $"{f.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {CreateFriendlyName(f.Name)}");

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
            source.AppendLine($"{{");
            source.IncreaseIndent();
        }

        var typeStack = new Stack<string>();

        var containingType = type.ContainingType;
        while (containingType is not null)
        {
            typeStack.Push(containingType.ToDisplayString());
            containingType = containingType.ContainingType;
        }

        var nestedCount = typeStack.Count;

        while (typeStack.Count > 0)
        {
            source.AppendLine($"partial class {typeStack.Pop()}");
            source.AppendLine($"{{");
            source.IncreaseIndent();
        }

        source.AppendLine($"partial class {type.ToDisplayString()}");
        source.AppendLine($"{{");
        source.IncreaseIndent();

        source.AppendLine($"public {type.Name}({string.Join(", ", parameters)})");
        source.AppendLine($"{{");
        source.IncreaseIndent();

        foreach (var item in fields)
        {
            source.AppendLine($"this.{item.Name} = {CreateFriendlyName(item.Name)};");
        }

        source.DecreaseIndent();
        source.AppendLine($"}}");

        source.DecreaseIndent();
        source.AppendLine($"}}");

        for (var i = 0; i < nestedCount; i++)
        {
            source.DecreaseIndent();
            source.AppendLine($"}}");
        }

        if (ns is not null)
        {
            source.DecreaseIndent();
            source.AppendLine($"}}");
        }

        return source.ToString();
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
