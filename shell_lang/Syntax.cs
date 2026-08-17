using System.Globalization;
using System.Text;

namespace ShellLang;

internal enum TokenKind
{
	End, Bad, NewLine, Semicolon, Identifier, Integer, Fractional, String, True, False, This,
	OpenParen, CloseParen, OpenBracket, CloseBracket, Comma, Dot, Colon,
	Assign, Arrow, InputArrow, Plus, Minus, Star, Slash, Percent, Bang,
	EqualEqual, BangEqual, Less, LessEqual, Greater, GreaterEqual, AndAnd, OrOr
}

internal sealed record Token(TokenKind Kind, string Text, object? Value, SourceSpan Span);

internal sealed class Lexer
{
	private readonly string _source;
	private readonly List<CompilationDiagnostic> _diagnostics;
	private int _position;
	public Lexer(string source, List<CompilationDiagnostic> diagnostics)
	{
		_source = source;
		_diagnostics = diagnostics;
	}

	public IReadOnlyList<Token> Lex()
	{
		var result = new List<Token>();
		while (_position < _source.Length)
		{
			var start = _position;
			var c = _source[_position];
			if (c is ' ' or '\t' or '\f')
			{
				_position++;
				continue;
			}
			if (c == '\r' || c == '\n')
			{
				if (c == '\r' && Peek(1) == '\n')
					_position += 2;
				else
					_position++;
				result.Add(Make(TokenKind.NewLine, start, _position));
				continue;
			}
			if (c == '#')
			{
				while (_position < _source.Length && _source[_position] is not '\r' and not '\n')
					_position++;
				continue;
			}
			if (char.IsLetter(c) || c == '_')
			{
				_position++;
				while (char.IsLetterOrDigit(Peek(0)) || Peek(0) == '_')
					_position++;
				var text = _source[start.._position];
				result.Add(new Token(text switch
				{
					"true" => TokenKind.True,
					"false" => TokenKind.False,
					"this" => TokenKind.This,
					_ => TokenKind.Identifier
				},
					text, text is "true" ? true : text is "false" ? false : null, Span(start, _position)));
				continue;
			}
			if (char.IsDigit(c))
			{
				_position++;
				while (char.IsDigit(Peek(0)))
					_position++;
				var fractional = false;
				if (Peek(0) == '.' && char.IsDigit(Peek(1)))
				{
					fractional = true;
					_position++;
					while (char.IsDigit(Peek(0)))
						_position++;
				}
				if (Peek(0) is 'e' or 'E')
				{
					fractional = true;
					_position++;
					if (Peek(0) is '+' or '-')
						_position++;
					if (!char.IsDigit(Peek(0)))
						AddDiagnostic("SL1002", "An exponent requires digits.", start, _position);
					while (char.IsDigit(Peek(0)))
						_position++;
				}
				var text = _source[start.._position];
				result.Add(new Token(fractional ? TokenKind.Fractional : TokenKind.Integer, text, text, Span(start, _position)));
				continue;
			}
			if (c == '"')
			{
				_position++;
				var value = new StringBuilder();
				var terminated = false;
				while (_position < _source.Length)
				{
					c = _source[_position++];
					if (c == '"')
					{
						terminated = true;
						break;
					}
					if (c is '\r' or '\n')
					{
						_position--;
						break;
					}
					if (c != '\\')
					{
						value.Append(c);
						continue;
					}
					if (_position >= _source.Length)
						break;
					var escaped = _source[_position++];
					if (escaped == 'u')
					{
						var hexStart = _position;
						var valid = true;
						for (var i = 0; i < 4; i++)
							if (!Uri.IsHexDigit(Peek(i)))
								valid = false;
						if (!valid)
							AddDiagnostic("SL1003", "A Unicode escape requires four hexadecimal digits.", _position - 2, Math.Min(_source.Length, _position + 4));
						else
						{
							value.Append((char)int.Parse(_source.AsSpan(hexStart, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
							_position += 4;
						}
					}
					else
					{
						value.Append(escaped switch
						{
							'n' => '\n',
							'r' => '\r',
							't' => '\t',
							'"' => '"',
							'\\' => '\\',
							_ => escaped
						});
						if (escaped is not ('n' or 'r' or 't' or '"' or '\\'))
							AddDiagnostic("SL1003", $"Unknown escape '\\{escaped}'.", _position - 2, _position);
					}
				}
				if (!terminated)
					AddDiagnostic("SL1004", "Unterminated string literal.", start, _position);
				result.Add(new Token(TokenKind.String, _source[start.._position], value.ToString(), Span(start, _position)));
				continue;
			}
			var kind = c switch
			{
				'(' => TokenKind.OpenParen,
				')' => TokenKind.CloseParen,
				'[' => TokenKind.OpenBracket,
				']' => TokenKind.CloseBracket,
				',' => TokenKind.Comma,
				'.' => TokenKind.Dot,
				':' => TokenKind.Colon,
				';' => TokenKind.Semicolon,
				'+' => TokenKind.Plus,
				'*' => TokenKind.Star,
				'/' => TokenKind.Slash,
				'%' => TokenKind.Percent,
				_ => TokenKind.Bad
			};
			var width = 1;
			if (c == '-' && Peek(1) == '>')
			{
				kind = TokenKind.Arrow;
				width = 2;
			}
			else if (c == '<' && Peek(1) == '-')
			{
				kind = TokenKind.InputArrow;
				width = 2;
			}
			else if (c == '=' && Peek(1) == '=')
			{
				kind = TokenKind.EqualEqual;
				width = 2;
			}
			else if (c == '!' && Peek(1) == '=')
			{
				kind = TokenKind.BangEqual;
				width = 2;
			}
			else if (c == '<' && Peek(1) == '=')
			{
				kind = TokenKind.LessEqual;
				width = 2;
			}
			else if (c == '>' && Peek(1) == '=')
			{
				kind = TokenKind.GreaterEqual;
				width = 2;
			}
			else if (c == '&' && Peek(1) == '&')
			{
				kind = TokenKind.AndAnd;
				width = 2;
			}
			else if (c == '|' && Peek(1) == '|')
			{
				kind = TokenKind.OrOr;
				width = 2;
			}
			else if (c == '=')
				kind = TokenKind.Assign;
			else if (c == '!')
				kind = TokenKind.Bang;
			else if (c == '<')
				kind = TokenKind.Less;
			else if (c == '>')
				kind = TokenKind.Greater;
			else if (c == '-')
				kind = TokenKind.Minus;
			_position += width;
			if (kind == TokenKind.Bad)
				AddDiagnostic("SL1001", $"Unexpected character '{c}'.", start, _position);
			result.Add(Make(kind, start, _position));
		}
		result.Add(new Token(TokenKind.End, string.Empty, null, Span(_position, _position)));
		return result;
	}

	private char Peek(int offset) => _position + offset < _source.Length ? _source[_position + offset] : '\0';
	private SourceSpan Span(int start, int end) => SourceSpan.FromBounds(_source, start, end);
	private Token Make(TokenKind kind, int start, int end) => new(kind, _source[start..end], null, Span(start, end));
	private void AddDiagnostic(string code, string message, int start, int end) => _diagnostics.Add(new(code, message, Span(start, end)));
}

internal abstract record SyntaxNode(SourceSpan Span);
internal sealed record ScriptSyntax(IReadOnlyList<StatementSyntax> Statements, SourceSpan Span) : SyntaxNode(Span);
internal abstract record StatementSyntax(SourceSpan Span) : SyntaxNode(Span);
internal sealed record AssignmentSyntax(string Name, ExpressionSyntax Expression, SourceSpan Span) : StatementSyntax(Span);
internal sealed record ExpressionStatementSyntax(ExpressionSyntax Expression, SourceSpan Span) : StatementSyntax(Span);
internal abstract record ExpressionSyntax(SourceSpan Span) : SyntaxNode(Span);
internal sealed record LiteralSyntax(Token Token) : ExpressionSyntax(Token.Span);
internal sealed record NameSyntax(string Name, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record ThisSyntax(SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record ArraySyntax(IReadOnlyList<ExpressionSyntax> Items, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record UnarySyntax(TokenKind Operator, ExpressionSyntax Operand, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record BinarySyntax(ExpressionSyntax Left, TokenKind Operator, ExpressionSyntax Right, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record ParenthesizedSyntax(ExpressionSyntax Expression, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record InvocationSyntax(string Name, IReadOnlyList<InvocationEntrySyntax> Entries, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record MemberSyntax(ExpressionSyntax Receiver, string Name, IReadOnlyList<InvocationEntrySyntax>? Arguments, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record ContextMemberSyntax(string Name, IReadOnlyList<InvocationEntrySyntax>? Arguments, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record PipelineSyntax(ExpressionSyntax Source, IReadOnlyList<ExpressionSyntax> Stages, SourceSpan Span) : ExpressionSyntax(Span);
internal enum InvocationEntryKind
{
	Positional, NamedArgument, ExplicitInput
}
internal sealed record InvocationEntrySyntax(InvocationEntryKind Kind, string? Name, ExpressionSyntax Expression, SourceSpan Span);

internal sealed class Parser
{
	private readonly string _source;
	private readonly IReadOnlyList<Token> _tokens;
	private readonly List<CompilationDiagnostic> _diagnostics;
	private int _position;
	private int _delimiterDepth;
	public Parser(string source, IReadOnlyList<Token> tokens, List<CompilationDiagnostic> diagnostics)
	{
		_source = source;
		_tokens = tokens;
		_diagnostics = diagnostics;
	}

	public ScriptSyntax ParseScript()
	{
		var statements = new List<StatementSyntax>();
		SkipTerminators();
		while (Current.Kind != TokenKind.End)
		{
			var start = Current.Span.Offset;
			StatementSyntax statement;
			if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Assign)
			{
				var name = Next();
				Next();
				SkipOperandNewlines();
				var expression = ParseExpression();
				statement = new AssignmentSyntax(name.Text, expression, Bounds(start, expression.Span.End));
			}
			else
			{
				var expression = ParseExpression();
				statement = new ExpressionStatementSyntax(expression, Bounds(start, expression.Span.End));
			}
			statements.Add(statement);
			if (Current.Kind is not (TokenKind.NewLine or TokenKind.Semicolon or TokenKind.End))
			{
				Error("SL1101", $"Expected a statement terminator, found '{Current.Text}'.", Current.Span);
				while (Current.Kind is not (TokenKind.NewLine or TokenKind.Semicolon or TokenKind.End))
					Next();
			}
			SkipTerminators();
		}
		return new ScriptSyntax(statements, Bounds(0, _source.Length));
	}

	private ExpressionSyntax ParseExpression() => ParsePipeline();
	private ExpressionSyntax ParsePipeline()
	{
		var source = ParseBinary(0);
		var stages = new List<ExpressionSyntax>();
		while (true)
		{
			SkipNewlinesBefore(TokenKind.Arrow);
			if (Current.Kind != TokenKind.Arrow)
				break;
			Next();
			SkipOperandNewlines();
			var stage = ParseStage();
			stages.Add(stage);
		}
		return stages.Count == 0 ? source : new PipelineSyntax(source, stages, Bounds(source.Span.Offset, stages[^1].Span.End));
	}

	private ExpressionSyntax ParseStage()
	{
		ExpressionSyntax result;
		if (Current.Kind != TokenKind.Identifier)
		{
			Error("SL1102", "A pipeline stage must name a command or intrinsic.", Current.Span);
			return ParsePrimary();
		}
		result = Peek(1).Kind == TokenKind.OpenParen ? ParseInvocation() : new NameSyntax(Next().Text, Previous.Span);
		while (true)
		{
			SkipNewlinesBefore(TokenKind.Dot);
			if (Current.Kind != TokenKind.Dot)
				break;
			result = ParseMemberSuffix(result);
		}
		return result;
	}

	private ExpressionSyntax ParseBinary(int parentPrecedence)
	{
		ExpressionSyntax left;
		var unary = UnaryPrecedence(Current.Kind);
		if (unary != 0 && unary >= parentPrecedence)
		{
			var op = Next();
			SkipOperandNewlines();
			var operand = ParseBinary(unary);
			left = new UnarySyntax(op.Kind, operand, Bounds(op.Span.Offset, operand.Span.End));
		}
		else
			left = ParsePostfix();
		while (true)
		{
			var precedence = BinaryPrecedence(Current.Kind);
			if (precedence == 0 || precedence <= parentPrecedence)
				break;
			var op = Next();
			SkipOperandNewlines();
			var right = ParseBinary(precedence);
			left = new BinarySyntax(left, op.Kind, right, Bounds(left.Span.Offset, right.Span.End));
		}
		return left;
	}

	private ExpressionSyntax ParsePostfix()
	{
		var result = ParsePrimary();
		while (true)
		{
			SkipNewlinesBefore(TokenKind.Dot);
			if (Current.Kind != TokenKind.Dot)
				break;
			result = ParseMemberSuffix(result);
		}
		return result;
	}

	private ExpressionSyntax ParseMemberSuffix(ExpressionSyntax receiver)
	{
		Next();
		SkipOperandNewlines();
		var name = Match(TokenKind.Identifier, "Expected a member name.");
		IReadOnlyList<InvocationEntrySyntax>? args = null;
		var end = name.Span.End;
		if (Current.Kind == TokenKind.OpenParen)
		{
			args = ParseEntries(TokenKind.CloseParen, out end);
		}
		return new MemberSyntax(receiver, name.Text, args, Bounds(receiver.Span.Offset, end));
	}

	private ExpressionSyntax ParsePrimary()
	{
		var token = Current;
		if (token.Kind is TokenKind.Integer or TokenKind.Fractional or TokenKind.String or TokenKind.True or TokenKind.False)
		{
			Next();
			return new LiteralSyntax(token);
		}
		if (token.Kind == TokenKind.Identifier)
			return Peek(1).Kind == TokenKind.OpenParen ? ParseInvocation() : new NameSyntax(Next().Text, token.Span);
		if (token.Kind == TokenKind.This)
		{
			Next();
			return new ThisSyntax(token.Span);
		}
		if (token.Kind == TokenKind.OpenParen)
		{
			var open = Next();
			_delimiterDepth++;
			SkipDelimiterNewlines();
			var expression = ParseExpression();
			SkipDelimiterNewlines();
			var close = Match(TokenKind.CloseParen, "Expected ')'.");
			_delimiterDepth--;
			return new ParenthesizedSyntax(expression, Bounds(open.Span.Offset, close.Span.End));
		}
		if (token.Kind == TokenKind.OpenBracket)
		{
			var open = Next();
			_delimiterDepth++;
			var items = new List<ExpressionSyntax>();
			SkipDelimiterNewlines();
			while (Current.Kind is not (TokenKind.CloseBracket or TokenKind.End))
			{
				items.Add(ParseExpression());
				SkipDelimiterNewlines();
				if (Current.Kind != TokenKind.Comma)
					break;
				Next();
				SkipDelimiterNewlines();
			}
			var close = Match(TokenKind.CloseBracket, "Expected ']'.");
			_delimiterDepth--;
			return new ArraySyntax(items, Bounds(open.Span.Offset, close.Span.End));
		}
		if (token.Kind == TokenKind.Dot)
		{
			var dot = Next();
			var name = Match(TokenKind.Identifier, "Expected a contextual member name.");
			IReadOnlyList<InvocationEntrySyntax>? args = null;
			var end = name.Span.End;
			if (Current.Kind == TokenKind.OpenParen)
				args = ParseEntries(TokenKind.CloseParen, out end);
			return new ContextMemberSyntax(name.Text, args, Bounds(dot.Span.Offset, end));
		}
		Error("SL1103", $"Expected an expression, found '{token.Text}'.", token.Span);
		Next();
		return new LiteralSyntax(new Token(TokenKind.Integer, "0", "0", token.Span));
	}

	private InvocationSyntax ParseInvocation()
	{
		var name = Match(TokenKind.Identifier, "Expected an invocation name.");
		var entries = ParseEntries(TokenKind.CloseParen, out var end);
		return new InvocationSyntax(name.Text, entries, Bounds(name.Span.Offset, end));
	}

	private IReadOnlyList<InvocationEntrySyntax> ParseEntries(TokenKind closeKind, out int end)
	{
		Match(TokenKind.OpenParen, "Expected '('.");
		_delimiterDepth++;
		SkipDelimiterNewlines();
		var entries = new List<InvocationEntrySyntax>();
		while (Current.Kind != closeKind && Current.Kind != TokenKind.End)
		{
			var start = Current.Span.Offset;
			InvocationEntryKind kind;
			string? name = null;
			if (Current.Kind == TokenKind.Identifier && Peek(1).Kind is TokenKind.Colon or TokenKind.InputArrow)
			{
				name = Next().Text;
				kind = Current.Kind == TokenKind.Colon ? InvocationEntryKind.NamedArgument : InvocationEntryKind.ExplicitInput;
				Next();
				SkipOperandNewlines();
			}
			else
				kind = InvocationEntryKind.Positional;
			var expression = ParseExpression();
			entries.Add(new(kind, name, expression, Bounds(start, expression.Span.End)));
			SkipDelimiterNewlines();
			if (Current.Kind != TokenKind.Comma)
				break;
			Next();
			SkipDelimiterNewlines();
		}
		var close = Match(closeKind, $"Expected '{(closeKind == TokenKind.CloseParen ? ")" : "]")}'.");
		_delimiterDepth--;
		end = close.Span.End;
		return entries;
	}

	private void SkipTerminators()
	{
		while (Current.Kind is TokenKind.NewLine or TokenKind.Semicolon)
			Next();
	}
	private void SkipDelimiterNewlines()
	{
		if (_delimiterDepth > 0)
			while (Current.Kind == TokenKind.NewLine)
				Next();
	}
	private void SkipOperandNewlines()
	{
		while (Current.Kind == TokenKind.NewLine)
			Next();
	}
	private void SkipNewlinesBefore(TokenKind kind)
	{
		var offset = 0;
		while (Peek(offset).Kind == TokenKind.NewLine)
			offset++;
		if (Peek(offset).Kind == kind)
			for (var i = 0; i < offset; i++)
				Next();
	}
	private Token Match(TokenKind kind, string message)
	{
		if (Current.Kind == kind)
			return Next();
		Error("SL1104", message, Current.Span);
		return new Token(kind, string.Empty, null, Current.Span with
		{
			Length = 0
		});
	}
	private Token Next()
	{
		var current = Current;
		if (_position < _tokens.Count - 1)
			_position++;
		return current;
	}
	private Token Current => Peek(0);
	private Token Previous => _tokens[Math.Max(0, _position - 1)];
	private Token Peek(int offset) => _tokens[Math.Min(_position + offset, _tokens.Count - 1)];
	private SourceSpan Bounds(int start, int end) => SourceSpan.FromBounds(_source, start, end);
	private void Error(string code, string message, SourceSpan span) => _diagnostics.Add(new(code, message, span));
	private static int UnaryPrecedence(TokenKind kind) => kind is TokenKind.Bang or TokenKind.Minus ? 7 : 0;
	private static int BinaryPrecedence(TokenKind kind) => kind switch
	{
		TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 6,
		TokenKind.Plus or TokenKind.Minus => 5,
		TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual => 4,
		TokenKind.EqualEqual or TokenKind.BangEqual => 3,
		TokenKind.AndAnd => 2,
		TokenKind.OrOr => 1,
		_ => 0
	};
}
