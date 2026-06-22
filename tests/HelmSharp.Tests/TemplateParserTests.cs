using HelmSharp.Engine;

namespace HelmSharp.Tests;

/// <summary>
/// Unit tests for the template tokenizer and parser — Issue #24.
/// </summary>
public class TemplateParserTests
{
    [Fact]
    public void Tokenize_EmptyTemplate_ReturnsOnlyEof()
    {
        var tokenizer = new TemplateTokenizer("");
        var tokens = tokenizer.TokenizeFlat().ToList();

        Assert.Single(tokens);
        Assert.Equal(TokenKind.EndOfFile, tokens[0].Kind);
    }

    [Fact]
    public void Tokenize_PlainText_ReturnsTextAndEof()
    {
        var tokenizer = new TemplateTokenizer("hello world");
        var tokens = tokenizer.TokenizeFlat().ToList();

        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Text, tokens[0].Kind);
        Assert.Equal("hello world", tokens[0].Value);
        Assert.Equal(TokenKind.EndOfFile, tokens[^1].Kind);
    }

    [Fact]
    public void Tokenize_SimpleAction_ReturnsCorrectTokens()
    {
        var tokenizer = new TemplateTokenizer("{{ .Values.foo }}");
        var tokens = tokenizer.TokenizeFlat().ToList();

        Assert.Equal(4, tokens.Count); // LeftDelim, ActionContent, RightDelim, EOF
        Assert.Equal(TokenKind.LeftDelim, tokens[0].Kind);
        Assert.False(tokens[0].LeftTrim);
        Assert.Equal(TokenKind.ActionContent, tokens[1].Kind);
        Assert.Contains(".Values.foo", tokens[1].Value);
        Assert.Equal(TokenKind.RightDelim, tokens[2].Kind);
        Assert.False(tokens[2].RightTrim);
    }

    [Fact]
    public void Tokenize_LeftTrimMarker_DetectedCorrectly()
    {
        var tokenizer = new TemplateTokenizer("{{- .Values.foo }}");
        var tokens = tokenizer.TokenizeFlat().ToList();

        Assert.Equal(TokenKind.LeftDelim, tokens[0].Kind);
        Assert.True(tokens[0].LeftTrim);
    }

    [Fact]
    public void Tokenize_RightTrimMarker_DetectedCorrectly()
    {
        var tokenizer = new TemplateTokenizer("{{ .Values.foo -}}");
        var tokens = tokenizer.TokenizeFlat().ToList();

        Assert.Equal(TokenKind.RightDelim, tokens[2].Kind);
        Assert.True(tokens[2].RightTrim);
    }

    [Fact]
    public void Tokenize_NestedActions_PreservesTextBetween()
    {
        var tokenizer = new TemplateTokenizer("before {{ .A }} middle {{ .B }} after");
        var tokens = tokenizer.TokenizeFlat().ToList();

        // Text("before "), LeftDelim, ActionContent, RightDelim,
        // Text(" middle "), LeftDelim, ActionContent, RightDelim,
        // Text(" after"), EOF = 10 tokens
        Assert.Equal(10, tokens.Count);
        Assert.Equal(TokenKind.Text, tokens[0].Kind);
        Assert.Contains("before", tokens[0].Value);
        Assert.Equal(TokenKind.Text, tokens[4].Kind);
        Assert.Contains("middle", tokens[4].Value);
    }

    [Fact]
    public void Tokenize_QuotedStringInsideAction_DoesNotSplit()
    {
        var tokenizer = new TemplateTokenizer("{{ include \"my.name\" . }}");
        var tokens = tokenizer.TokenizeFlat().ToList();

        Assert.Equal(TokenKind.ActionContent, tokens[1].Kind);
        Assert.Contains("my.name", tokens[1].Value);
    }

    [Fact]
    public void Tokenize_Comment_KeptAsActionContent()
    {
        var tokenizer = new TemplateTokenizer("{{/* this is a comment */}}");
        var tokens = tokenizer.TokenizeFlat().ToList();

        Assert.Equal(TokenKind.ActionContent, tokens[1].Kind);
        Assert.Contains("/*", tokens[1].Value);
    }

    [Fact]
    public void Parse_EmptyTemplate_ReturnsDocumentWithNoChildren()
    {
        var tokens = new TemplateTokenizer("").TokenizeFlat();
        var parser = new TemplateParser(tokens);
        var doc = parser.Parse();

        Assert.Empty(doc.Children);
    }

    [Fact]
    public void Parse_PlainText_ReturnsSingleTextNode()
    {
        var tokens = new TemplateTokenizer("hello").TokenizeFlat();
        var parser = new TemplateParser(tokens);
        var doc = parser.Parse();

        Assert.Single(doc.Children);
        Assert.IsType<TextNode>(doc.Children[0]);
        Assert.Equal("hello", ((TextNode)doc.Children[0]).Content);
    }

    [Fact]
    public void Parse_SimpleAction_ReturnsActionNode()
    {
        var tokens = new TemplateTokenizer("{{ .Values.foo }}").TokenizeFlat();
        var parser = new TemplateParser(tokens);
        var doc = parser.Parse();

        Assert.Single(doc.Children);
        Assert.IsType<ActionNode>(doc.Children[0]);
        var action = (ActionNode)doc.Children[0];
        Assert.Contains(".Values.foo", action.Expression);
    }

    [Fact]
    public void Parse_IfBlock_ReturnsBlockNodeWithCondition()
    {
        var tokens = new TemplateTokenizer("{{ if .Values.enabled }}yes{{ end }}").TokenizeFlat();
        var parser = new TemplateParser(tokens);
        var doc = parser.Parse();

        Assert.Single(doc.Children);
        var block = Assert.IsType<BlockNode>(doc.Children[0]);
        Assert.Equal("if", block.Keyword);
        Assert.Contains(".Values.enabled", block.Expression);
        Assert.IsType<TemplateDocumentNode>(block.TrueBody);
        Assert.Empty(block.ElseIfChain);
    }

    [Fact]
    public void Parse_NestedIfBlocks_ProducesNestedAst()
    {
        var template = "{{ if .A }}outer{{ if .B }}inner{{ end }}{{ end }}";
        var tokens = new TemplateTokenizer(template).TokenizeFlat();
        var parser = new TemplateParser(tokens);
        var doc = parser.Parse();

        var outer = Assert.IsType<BlockNode>(doc.Children[0]);
        var trueDoc = Assert.IsType<TemplateDocumentNode>(outer.TrueBody);
        // trueDoc should have: Text("outer"), inner BlockNode
        Assert.Equal(2, trueDoc.Children.Count);
        var inner = Assert.IsType<BlockNode>(trueDoc.Children[1]);
        Assert.Equal("if", inner.Keyword);
        Assert.Contains(".B", inner.Expression);
    }

    [Fact]
    public void Parse_DefineBlock_ExtractedToRegistry()
    {
        var template = "{{ define \"mytpl\" }}body content{{ end }}";
        var tokens = new TemplateTokenizer(template).TokenizeFlat();
        var parser = new TemplateParser(tokens);
        var doc = parser.Parse();

        Assert.Empty(doc.Children); // defines are extracted, not in document
        Assert.True(parser.Defines.ContainsKey("mytpl"),
            $"Define 'mytpl' not found. Keys: {string.Join(", ", parser.Defines.Keys)}");
        var define = parser.Defines["mytpl"];
        Assert.Equal("mytpl", define.Name);
    }

    [Fact]
    public void Parse_IfWithElseBlock_CorrectStructure()
    {
        var template = "{{ if .A }}yes{{ else }}no{{ end }}";
        var tokens = new TemplateTokenizer(template).TokenizeFlat();
        var parser = new TemplateParser(tokens);
        var doc = parser.Parse();

        var block = Assert.IsType<BlockNode>(doc.Children[0]);
        Assert.NotEmpty(((TemplateDocumentNode)block.TrueBody).Children);
        Assert.NotEmpty(((TemplateDocumentNode)block.FalseBody).Children);
    }

    [Fact]
    public void Parse_ElseIfChain_CorrectStructure()
    {
        var template = "{{ if .A }}a{{ else if .B }}b{{ else if .C }}c{{ else }}d{{ end }}";
        var tokens = new TemplateTokenizer(template).TokenizeFlat();
        var parser = new TemplateParser(tokens);
        var doc = parser.Parse();

        var block = Assert.IsType<BlockNode>(doc.Children[0]);
        Assert.Equal("if", block.Keyword);
        Assert.Equal(2, block.ElseIfChain.Count);
        Assert.Contains(".B", block.ElseIfChain[0].Condition);
        Assert.Contains(".C", block.ElseIfChain[1].Condition);
        Assert.NotEmpty(((TemplateDocumentNode)block.FalseBody).Children);
    }

    [Fact]
    public void Parse_WithBlock_ReturnsBlockNode()
    {
        var template = "{{ with .Values.data }}has data{{ end }}";
        var tokens = new TemplateTokenizer(template).TokenizeFlat();
        var parser = new TemplateParser(tokens);
        var doc = parser.Parse();

        var block = Assert.IsType<BlockNode>(doc.Children[0]);
        Assert.Equal("with", block.Keyword);
    }

    [Fact]
    public void Parse_RangeBlock_ReturnsBlockNode()
    {
        var template = "{{ range .Values.items }}item{{ end }}";
        var tokens = new TemplateTokenizer(template).TokenizeFlat();
        var parser = new TemplateParser(tokens);
        var doc = parser.Parse();

        var block = Assert.IsType<BlockNode>(doc.Children[0]);
        Assert.Equal("range", block.Keyword);
    }

    [Fact]
    public void Parse_TrimMarkers_PreservedOnBlockNodes()
    {
        var template = "{{- if .A }}yes{{ end }}";
        var tokens = new TemplateTokenizer(template).TokenizeFlat();
        var parser = new TemplateParser(tokens);
        var doc = parser.Parse();

        var block = Assert.IsType<BlockNode>(doc.Children[0]);
        Assert.True(block.LeftTrim);
    }

    [Fact]
    public void Parse_ElseIfChain_PreservesTrimMarkerPerBranch()
    {
        // {{- else if .B }} has left trim, {{ else if .C }} does not
        var template = "{{ if .A }}a{{- else if .B }}b{{ else if .C }}c{{ end }}";
        var tokens = new TemplateTokenizer(template).TokenizeFlat();
        var parser = new TemplateParser(tokens);
        var doc = parser.Parse();

        var block = Assert.IsType<BlockNode>(doc.Children[0]);
        Assert.Equal(2, block.ElseIfChain.Count);
        Assert.True(block.ElseIfChain[0].TrimMarker,
            "first else-if has {{- (left trim), TrimMarker should be true");
        Assert.False(block.ElseIfChain[1].TrimMarker,
            "second else-if has {{ (no trim), TrimMarker should be false");
    }

    [Fact]
    public void GetFirstWord_ElseIf_RecognizedAsCompound()
    {
        Assert.Equal("else if", TemplateParser.GetFirstWord("else if .A"));
        Assert.Equal("else if", TemplateParser.GetFirstWord("  else if .A"));
    }

    [Fact]
    public void GetFirstWord_PlainElse_NotConfusedWithElseIf()
    {
        Assert.Equal("else", TemplateParser.GetFirstWord("else"));
        Assert.Equal("else", TemplateParser.GetFirstWord(" else "));
    }

    [Fact]
    public void GetFirstWord_FunctionName_ReturnsFirstWord()
    {
        Assert.Equal("include", TemplateParser.GetFirstWord("include \"tpl\" ."));
        Assert.Equal("if", TemplateParser.GetFirstWord("if .Values.a"));
        Assert.Equal("toJson", TemplateParser.GetFirstWord("toJson .Values.data"));
    }

    [Fact]
    public void SerializeToText_ActionNode_Roundtrips()
    {
        var doc = new TemplateDocumentNode
        {
            Children =
            {
                new TextNode { Content = "before " },
                new ActionNode { Expression = ".Values.foo", LeftTrim = false, RightTrim = false },
                new TextNode { Content = " after" },
            }
        };

        var text = doc.SerializeToText();
        Assert.Equal("before {{ .Values.foo }} after", text);
    }

    [Fact]
    public void SerializeToText_PreservesTrimMarkers()
    {
        var doc = new TemplateDocumentNode
        {
            Children =
            {
                new ActionNode { Expression = "expr", LeftTrim = true, RightTrim = true },
            }
        };

        var text = doc.SerializeToText();
        Assert.Equal("{{- expr -}}", text);
    }

    [Fact]
    public void Parse_Comment_CreatesCommentNode()
    {
        var tokens = new TemplateTokenizer("{{/* this is a comment */}}").TokenizeFlat();
        var parser = new TemplateParser(tokens);
        var doc = parser.Parse();

        Assert.Single(doc.Children);
        var comment = Assert.IsType<CommentNode>(doc.Children[0]);
        Assert.Contains("/*", comment.Content);
    }

    [Fact]
    public void Parse_ActionNode_HasPositionSet()
    {
        var tokens = new TemplateTokenizer("{{ .Values.foo }}").TokenizeFlat();
        var parser = new TemplateParser(tokens);
        var doc = parser.Parse();

        var action = Assert.IsType<ActionNode>(doc.Children[0]);
        Assert.True(action.StartOffset >= 0);
        Assert.True(action.StartLine > 0);
    }

    [Fact]
    public void Tokenize_MultiLineContent_TracksCorrectStartPositions()
    {
        var tokenizer = new TemplateTokenizer("line1\n{{ .A }}\nline2\n{{ .B }}");
        var tokens = tokenizer.TokenizeFlat().ToList();

        // First text token should start at line 1
        var text1 = tokens.First(t => t.Kind == TokenKind.Text && t.Value.Contains("line1"));
        Assert.Equal(1, text1.Line);

        // Second text token starts after `}}` on line 2 (includes the \n before line3's "line2")
        var text2 = tokens.First(t => t.Kind == TokenKind.Text && t.Value.Contains("line2"));
        Assert.Equal(2, text2.Line);
    }
}
