using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

public class ParseException: Exception
{
	public int Offset { get; private set; }
	
	public ParseException(int offset, string message): base(message)
	{
		this.Offset = offset;
	}
}

public abstract class Node
{
}

public class WhiteSpace: Node
{
	public string RawWhiteSpace { get; set; }
}

public class Comment: Node
{
	public string RawComment { get; set; }
	public string Text { get; set; }
}

public class StringValue: Node
{
	public string RawText { get; set; }
	public string Text { get; set; }
}

public class ObjectValue: Node
{
	public string RawName { get; set; }
	public string Name { get; set; }
	public string RawWhiteSpace { get; set; }
	public List<Node> Content { get; set; }
}

public class KeyValuePair: Node
{
	public string RawKey { get; set; }
	public string Key { get; set; }
	public string RawEquals { get; set; }
	public Node Value { get; set; }
}

public class Separator: Node
{
	public char RawSeparator { get; set; }
}

public class Conf
{
	int curr;
	string input;
	List<Node> output;
	
	public Conf(string filename)
	{
		curr = 0;
		this.input = File.ReadAllText(filename);
		this.output = ReadContent();
	}
	
	public void DeserializeTo(Type type)
	{
		foreach (Node node in output) {
			KeyValuePair kvp = node as KeyValuePair;
			if (kvp != null) {
				FieldInfo field = type.GetField(kvp.RawKey, BindingFlags.Public | BindingFlags.Static);
				if (field != null) {
					string str = ((StringValue)kvp.Value).RawText;
					str = UnQuote(str);
					if (field.FieldType == typeof(TimeSpan)) {
						TimeSpan val;
						TimeSpan.TryParse(str, out val);
						field.SetValue(null, val);
					} else {
						object val = Convert.ChangeType(str, field.FieldType);
						field.SetValue(null, val);
					}
				}
			}
		}
	}
	
	static string UnQuote(string str)
	{
		if (string.IsNullOrEmpty(str))
			return str;
		
		char first = str[0];
		if (first != '"' && first != '\'')
			return str;
		
		StringBuilder sb = new StringBuilder(str.Length);
		int curr = 1;
		while (true) {
			if (curr == str.Length) break;
			int esc = str.IndexOf('\\', curr);
			if (esc != -1) {
				sb.Append(str, curr, esc - curr);
				curr = esc + 1;
				if (curr < str.Length) {
					char id = str[curr];
					curr++;
					switch (id) {
						case 'n':  sb.Append('\n'); break;
						case 'r':  sb.Append('\r'); break;
						case 't':  sb.Append('\t'); break;
						case '"':  sb.Append('\"'); break;
						case '\'': sb.Append('\''); break;
						case 'u':
							// TODO: EOF
							string code = str.Substring(curr, 4);
							curr += 4;
							int codeNo = int.Parse(code, NumberStyles.HexNumber);
							sb.Append((char)codeNo);
							break;
						default: sb.Append(id); break;
					}
				} else {
					sb.Append('\\');
					break;
				}
			} else {
				sb.Append(str, curr, str.Length - 1 - curr);
				if (str[str.Length - 1] != first)
					sb.Append(str[str.Length - 1]);
				break;
			}
		}
		return sb.ToString();
	}
	
	string ReadWhiteSpace()
	{
		int start = curr;
		while(true) {
			if (curr == input.Length) break;
			char c = input[curr];
			if (c == ' ' || c == '\t' || c == '\r' || c == '\n') {
				curr++;
			} else {
				break;
			}
		}
		return input.Substring(start, curr - start);
	}
	
	string ReadComment()
	{
		int start = curr;
		if (input[curr] == '#') {
			curr++;
		} else {
			return string.Empty;
		}
		while(true) {
			if (curr == input.Length) break;
			char c = input[curr];
			if (c == '\r') {
				curr++;
				if (curr < input.Length && input[curr] == '\n')
					curr++;
				break;
			} else if (c == '\n') {
				curr++;
				break;
			} else {
				curr++;
				continue;
			}
		}
		return input.Substring(start, curr - start);
	}
	
	void ReadStringAndWhitespace(out string str, out string ws)
	{
		int start = curr;
		char first = input[curr];
		if (first == '"' || first == '\'') {
			// Quoted string
			curr++;
			while(true) {
				if (curr == input.Length) break;
				int endQuote = input.IndexOf(first, curr);
				if (endQuote == -1)
					throw new ParseException(start, "Closing quote is missing");
				curr = endQuote + 1;
				if (input[endQuote - 1] == '\\') {
					// The quote was escaped
					continue;
				} else {
					break;
				}
			}
			str = input.Substring(start, curr - start);
			ws = ReadWhiteSpace();
			return;
		} else {
			// Unquoted string
			while(true) {
				if (curr == input.Length) break;
				char c = input[curr];
				// Quick path
				if (('A' <= c && c <= 'z') ||
				    ('0' <= c && c <= '9') ||
				    ((c == ' ' || c == '\t') && curr != start))
				{
					curr++;
					continue;
				}
				if (c == '\'' || c == '"' || c == '{' || c == '}' || c == '=' || c == ';' || c == ',' || c == '#' || c == '\r' || c == '\n' || c == ' ' || c == '\t') {
					// Character is forbiden in unquoted string
					break;
				} else {
					curr++;
					continue;
				}
			}
			// String must not end with whitesapce
			int nonWhiteSpace = curr - 1;
			while(nonWhiteSpace >= start && (input[nonWhiteSpace] == ' ' || input[nonWhiteSpace] == '\t')) {
				nonWhiteSpace--;
			}
			int whiteSpaceStart = nonWhiteSpace + 1;
			
			str = input.Substring(start, whiteSpaceStart - start);
			ws = input.Substring(whiteSpaceStart, curr - whiteSpaceStart);
			return;
		}
	}
	
	List<Node> ReadContent()
	{
		List<Node> content = new List<Node>();
		bool done = false;
		while(!done) {
			if (curr == input.Length) break;
			char c = input[curr];
			switch(c) {
				case ' ':
				case '\t':
				case '\r':
				case '\n':
					content.Add(new WhiteSpace() { RawWhiteSpace = ReadWhiteSpace() });
					break;
				case '{':
					content.Add(new ObjectValue() { Content = ReadCurlyBrackets() });
					break;
				case '}':
					done = true;
					break;
				case ';':
				case ',':
					content.Add(new Separator() { RawSeparator = c });
					curr++;
					break;
				case '=':
					throw new ParseException(curr, "Equals must be preceded by key");
				case '#':
					content.Add(new Comment() { RawComment = ReadComment() });
					break;
				default:
					string str;
					string ws1;
					ReadStringAndWhitespace(out str, out ws1);
					if (curr < input.Length) {
						c = input[curr];
					} else {
						c = '\0';
					}
					if (c == '=') {
						curr++;
						string ws2 = ReadWhiteSpace();
						KeyValuePair kvp = new KeyValuePair() { RawKey = str, RawEquals = ws1 + "=" + ws2 };
						// TODO: Is proper string?
						string val;
						string ws3;
						ReadStringAndWhitespace(out val, out ws3);
						if (curr < input.Length && input[curr] == '{') {
							kvp.Value = new ObjectValue() { RawName = val, RawWhiteSpace = ws3, Content = ReadCurlyBrackets() };
							content.Add(kvp);
						} else {
							kvp.Value = new StringValue() { RawText = val };
							if (ws3 != string.Empty) {
								content.Add(new WhiteSpace() { RawWhiteSpace = ws3 });
							}
							content.Add(kvp);
						}
					} else if (c == '{') {
						content.Add(new ObjectValue() { RawName = str, RawWhiteSpace = ws1, Content = ReadCurlyBrackets() } );
					} else {
						content.Add(new StringValue() { RawText = str } );
						if (ws1 != string.Empty) {
							content.Add(new WhiteSpace() { RawWhiteSpace = ws1 } );
						}
					}
					break;
			}
		}
		return content;
	}
	
	List<Node> ReadCurlyBrackets()
	{
		Debug.Assert(input[curr] == '{');
		curr++;
		List<Node> content = ReadContent();
		if (curr < input.Length && input[curr] == '}') {
			curr++;
		} else {
			throw new ParseException(curr, "Expected '}'");
		}
		return content;
	}
	
	// (1) Content ::= (WhiteSpace | Comment | String | Object | KeyValuePair | ';'| ',')*
	// (2) WhiteSpace ::= (' ' | '\t' | '\r' | '\n')+
	// (3) Comment ::= '#' .* ('\r\n' | '\r' | '\n')
	// (4) String ::= Char [(Char | ' ' | '\t')* Char] | QuotedString
	// (5) Object ::= [String [WhiteSpace]] '{' Content '}'
	// (6) KeyValuePair ::= String [WhiteSpace] '=' [WhiteSpace] (String | Object)
	// (7) Char ::= . - ("'" | '"' | '{' | '}' | '=' | ';' | ',' | '#' | '\r' | '\n' | ' ' | '\t')
	// (8) QuotedString ::= "'" ("\\'" | "''" | . - "'")* "'" | '"' ('\\"' | '""' | . - '"')* '"'
}