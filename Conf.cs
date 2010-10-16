using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Conf
{
	/// <summary>
	///
	/// </summary>
	/// <remarks>
	///  Syntax:
	///   (1) Arguments ::= WS? (Comment | Argument) (WS (Comment | Argument))* WS? | WS?
	///   (2) Comment   ::= '#' [^\r\n]*
	///   (3) WS        ::= (' ' | '\t' | '\r' | '\n')+
	///   (4) String    ::= [^\r\n\t #'"{}=]+ | ("'" [^']* "'") | ('"' [^"]* '"')
	///   (5) Argument  ::= [String WS? '=' WS?] String? [WS? '{' Arguments '}']
	/// </remarks>
	public class Parser
	{
		string input;
		int curr;
		int length;

		Parser(string input)
		{
			this.input  = input;
			this.curr   = 0;
			this.length = input.Length;
		}

		public static List<Argument> Parse(string text)
		{
			return new Parser(text).ReadArguments(false);
		}

		public static List<Argument> ParseCommandLine()
		{
			Parser parser = new Parser(Environment.CommandLine);
			string arg0;
			parser.ReadString(out arg0); // Skip the name of the executable
			return parser.ReadArguments(false);
		}

		List<Argument> ReadArguments(bool nested)
		{
			List<Argument> args = new List<Argument>();

			ReadWhiteSpace();

			while (curr < length) {
				// End of nested argument list
				if (input[curr] == '}') {
					if (nested) {
						break;
					} else {
						throw new ParseException(curr, curr, "Superfluous '}'");
					}
				}

				// Try to parse a comment
				if (ReadComment()) {
					ReadWhiteSpace();
				} else {
					// If it is not a comment, then it must be an argument
					Argument arg = new Argument();

					// Consider this a primitive stack of the last seen but unprocessed tokens
					string str;
					bool haveString;
					bool haveWhiteSpace;

					// Try to push string and whitespace on our mini parsing stack
					haveString = ReadString(out str);
					haveWhiteSpace = haveString ? ReadWhiteSpace() : false;

					// Set the name
					if (haveString && curr < length && input[curr] == '=') {
						// Pop the string and whitespace from the stack
						arg.Name = str;
						haveString = haveWhiteSpace = false;
						curr++; // '='
						ReadWhiteSpace();
						// Repopulate the empty stack
						haveString = ReadString(out str);
						haveWhiteSpace = haveString ? ReadWhiteSpace() : false;
					} else {
						arg.Name = string.Empty;
					}

					bool valueSet = false;

					// Set the value
					if (haveString) {
						// Pop string from stack
						arg.Value = str;
						haveString = false;
						valueSet = true;
					} else {
						arg.Value = string.Empty;
					}

					// Set the value's arguments
					if (curr < length && input[curr] == '{') {
						Debug.Assert(!haveString);
						haveWhiteSpace = false; // Pop whitespace from stack
						curr++; // '{'
						arg.ValueArguments = ReadArguments(true);
						if (curr == length || input[curr] != '}')
							throw new ParseException(curr, curr, "'}' Expected");
						curr++; // '}'
						valueSet = true;
					} else {
						arg.ValueArguments = null;
					}

					// At least string or argument must be present
					if (!valueSet)
						throw new ParseException(curr, curr, "String value or argument list expected");

					// Argument was successfully read
					Debug.Assert(!haveString);
					args.Add(arg);

					// The argument has to be followed by whitespace or end of file or '}'
					if (!haveWhiteSpace)
						haveWhiteSpace = ReadWhiteSpace();
					if (!haveWhiteSpace && curr < length && input[curr] != '}') {
						// Try to produce nice error message
						char c = input[curr];
						if (arg.ValueArguments == null && (c == '#' || c == '\'' || c == '"'))
							throw new ParseException(curr, curr, "The character " + c + " is not allowed in string");
						throw new ParseException(curr, curr, "Argument has to be followed by whitespace");
					}
				}
			}

			return args;
		}

		bool ReadWhiteSpace()
		{
			int start = curr;
			while (curr < length && ((input[curr] <= ' ') && (input[curr] == ' ' || input[curr] == '\t' || input[curr] == '\r' || input[curr] == '\n'))) curr++;
			return curr > start;
		}

		bool ReadComment()
		{
			if (curr < length && input[curr] == '#') {
				curr++; // '#'
				while (curr < length && input[curr] != '\r' && input[curr] != '\n') curr++;
				return true;
			} else {
				return false;
			}
		}

		bool ReadString(out string str)
		{
			if (curr == length) {
				str = string.Empty;
				return false;
			}

			int start = curr;
			char first = input[curr];
			if (first == '"' || first == '\'') {
				// Quoted string
				curr++;
				int endQuote;
				if (curr == length || (endQuote = input.IndexOf(first, curr)) == -1)
					throw new ParseException(start, length, "Closing quote is missing");
				curr = endQuote + 1;
				str = ReslveReferences(start + 1, endQuote);
				return true;
			} else {
				// Unquoted string
				while (curr < length) {
					char c = input[curr];
					if (('A' <= c && c <= 'z') || ('0' <= c && c <= '9') || c > 0x80) {
						curr++; // Quick pass test for the most common characters
					} else {
						if (c == ' ' || c == '=' || c == '\r' || c == '\n' || c == '\t' || c == '{' || c == '}' || c == '#' || c == '\'' || c == '"')
							break;  // String terminator
						curr++; // Less common characters
					}
				}
				str = ReslveReferences(start, curr);
				return curr > start;
			}
		}

		string ReslveReferences(int start, int end)
		{
			if (start == end)
				return string.Empty;

			if (input.IndexOf('&', start, end - start) == -1)
				return input.Substring(start, end - start);

			StringBuilder sb = new StringBuilder(end - start);
			int pos = start;
			while (pos < end) {
				int amp = input.IndexOf('&', pos, end - pos);
				if (amp == -1) {
					sb.Append(input, pos, end - pos);
					break;
				} else {
					sb.Append(input, pos, amp - pos);
					int semicolon = input.IndexOf(';', amp, end - amp);
					if (semicolon == -1)
						throw new ParseException(amp, end, "';' is missing after '&'");
					string refName = input.Substring(amp + 1, semicolon - amp - 1);
					switch (refName) {
						case "":     throw new ParseException(amp, amp + 2, "Empty reference");
						case "amp":  sb.Append('&'); break;
						case "apos": sb.Append('\''); break;
						case "quot": sb.Append('\"'); break;
						case "lt":   sb.Append('<'); break;
						case "gt":   sb.Append('>'); break;
						case "br":   sb.Append('\n'); break;
						case "tab":  sb.Append('\t'); break;
						case "nbsp": sb.Append((char)0xA0); break;
						default:
							// Unicode character
							int utf32;
							try {
								if (refName.StartsWith("#x")) {
									utf32 = int.Parse(refName.Substring(2), NumberStyles.AllowHexSpecifier);
								} else if (refName[0] == '#') {
									utf32 = int.Parse(refName.Substring(1), NumberStyles.None);
								} else {
									utf32 = int.Parse(refName, NumberStyles.AllowHexSpecifier);
								}
							} catch {
								throw new ParseException(amp + 1, semicolon, "Failed to parse reference '" + refName + "'");
							}
							try {
								sb.Append(char.ConvertFromUtf32(utf32));
							} catch {
								throw new ParseException(amp + 1, semicolon, "Invalid Unicode code point " + refName);
							}
							break;
					}
					pos = semicolon + 1;
				}
			}
			return sb.ToString();
		}
	}

	public class ParseException : Exception
	{
		public int Start { get; private set; }
		public int End { get; private set; }

		public ParseException(int start, int end, string message)
			: base(message)
		{
			this.Start = start;
			this.End = end;
		}
	}

	public class Argument
	{
		public string Name { get; set; }
		public string Value { get; set; }
		public List<Argument> ValueArguments { get; set; }
	}

	public static class Deserializer
	{
		public static void LoadAppConfig<T>()
		{
			// Set properties from the .conf file
			string assembyPath = Assembly.GetEntryAssembly().Location;
			string confPath = Path.Combine(Path.GetDirectoryName(assembyPath), Path.GetFileNameWithoutExtension(assembyPath) + ".conf");
			if (File.Exists(confPath)) {
				string confText = File.ReadAllText(confPath);
				Deserialize(Parser.Parse(confText), default(T));
			}

			// Set properties from command line
			Deserialize(Parser.ParseCommandLine(), default(T));
		}

		public static void Deserialize<T>(List<Argument> from, T to)
		{
			foreach (Argument arg in from) {
				FieldInfo field = typeof(T).GetField(arg.Name, BindingFlags.Public | BindingFlags.Static);
				if (field != null) {
					if (field.FieldType == typeof(TimeSpan)) {
						TimeSpan val;
						TimeSpan.TryParse(arg.Value, out val);
						field.SetValue(null, val);
					}
					else {
						object val = Convert.ChangeType(arg.Value, field.FieldType);
						field.SetValue(null, val);
					}
				}
			}
		}
	}
}