using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Conf
{
    public class Node
    {
        public string     Value    { get; set; }
        public List<Node> Children { get; set; }
    }

    /// <remarks>
    /// WS       ::= (' ' | '\t' | '\r' | '\n' | '#' [^\r\n]*)*
    /// String   ::= '"' ([^"] | '""')* '"' | "'" ([^'] | "''")* "'" | [^ \r\n\t#={}'"]+
    /// Node     ::= String? [WS? '{' Children '}' | WS? '=' WS? Node]
    /// Document ::= WS? (Node WS?)*
    /// </remarks>
    public class Parser
    {
        string input;
        int curr;
        int end;
        int endOfLastString;

        Parser(string input)
        {
            this.input = input;
            this.curr = 0;
            this.end = input.Length;
            this.endOfLastString = -1;
        }

        public static List<Node> Parse(string input)
        {
            return new Parser(input).ReadDocument();
        }

        /// <remarks> WS ::= (' ' | '\t' | '\r' | '\n' | '#' [^\r\n]*)* </remarks>
        void ReadWhiteSpace()
        {
            while (curr < end) {
                switch(input[curr]) {
                    case ' ':
                    case '\t':
                    case '\r':
                    case '\n':
                        curr++; // Whitespace
                        break;
                    case '#':
                        curr++; // Comment
                        while (curr < end && input[curr] != '\r' && input[curr] != '\n') curr++;
                        break;
                    default:
                        return;
                }
            }
        }

        /// <remarks> String ::= '"' ([^"] | '""')* '"' | "'" ([^'] | "''")* "'" | [^ \r\n\t#={}'"]+ </remarks>
        string ReadString()
        {
            if (curr == end)
                throw new ParseException(curr, curr, "String expected");
            int start = curr;
            char quote = input[curr];
            if (quote == '"' || quote == '\'') {
                // Quoted string
                do {
                    curr++; // Quote
                    while (curr < end && input[curr] != quote) curr++;
                    if (curr == end)
                        throw new ParseException(start, end, "Closing quote is missing");
                    curr++; // Quote
                } while (curr < end && input[curr] == quote); // Double-quote - continue
                return input.Substring(start + 1, curr - start - 2).Replace(quote.ToString() + quote.ToString(), quote.ToString());
            } else {
                // Unquoted string
                while (curr < end) {
                    switch(input[curr]) {
                        case ' ': case '\r': case '\n': case '\t': case '#': case '=': case '{': case '}': case '\'': case '"':
                            if (curr == start)
                                throw new ParseException(curr, curr, "String expected");
                            return input.Substring(start, curr - start); // End of string
                    }
                    curr++;
                }
                return input.Substring(start, curr - start); // End of input
            }
        }

        /// <remarks> String? [WS? '{' Children '}' | WS? '=' WS? Node] </remarks>
        Node ReadNode()
        {
            Node node = new Node();
            // Read the node value (optional)
            if (curr < end && input[curr] == '{') {
                node.Value = string.Empty;
            } else {
                if (curr == endOfLastString)
                    throw new ParseException(curr, curr, "Two consecutive strings have to be separated by whitespace.");
                node.Value = ReadString();
                endOfLastString = curr;
                ReadWhiteSpace();
            }
            // Read the children (optional)
            if (curr < end && input[curr] == '=') {
                curr++; // '='
                ReadWhiteSpace();
                node.Children = new List<Node>(1);
                node.Children.Add(ReadNode());
            } else if (curr < end && input[curr] == '{') {
                curr++; // '{'
                node.Children = new List<Node>();
                ReadWhiteSpace();
                while (curr < end && input[curr] != '}') {
                    node.Children.Add(ReadNode());
                    ReadWhiteSpace();
                }
                if (!(curr < end && input[curr] == '}'))
                    throw new ParseException(curr, curr, "'}' Expected");
                curr++; // '}'
            }
            return node;
        }

        /// <remarks> Document ::= WS? (Node WS?)* </remarks>
        List<Node> ReadDocument()
        {
            List<Node> nodes = new List<Node>();
            ReadWhiteSpace();
            while (curr < end) {
                nodes.Add(ReadNode());
                ReadWhiteSpace();
            }
            return nodes;
        }
    }

    public class ParseException : Exception
    {
        public int Start { get; private set; }
        public int End { get; private set; }

        public ParseException(int start, int end, string message) : base(message)
        {
            this.Start = start;
            this.End = end;
        }
    }

    public class PrettyPrinter
    {
        StringBuilder sb = new StringBuilder();
        int depth = 0;

        public static string Print(IEnumerable<Node> nodes)
        {
            PrettyPrinter pp = new PrettyPrinter();
            pp.Append(nodes);
            return pp.sb.ToString();
        }

        void Append(IEnumerable<Node> nodes)
        {
            foreach (Node node in nodes) {
                // Delimiter
                if (sb.Length > 0) {
                    sb.Append('\n');
                    sb.Append(' ', depth * 2);
                }
                if (node.Children == null || node.Children.Count == 0) {
                    sb.Append(Escape(node.Value));
                } else if (node.Children.Count == 1 && (node.Children[0].Children == null || node.Children[0].Children.Count == 0) && !string.IsNullOrEmpty(node.Value)) {
                    sb.Append(Escape(node.Value));
                    sb.Append(" = ");
                    sb.Append(Escape(node.Children[0].Value));
                } else {
                    if (!string.IsNullOrEmpty(node.Value)) {
                        sb.Append(Escape(node.Value));
                        sb.Append(' ');
                    }
                    sb.Append('{');
                    depth++;
                    Append(node.Children);
                    depth--;
                    sb.Append('\n');
                    sb.Append(' ', depth * 2);
                    sb.Append('}');
                }
            }
        }

        public static string Escape(string val)
        {
            if (string.IsNullOrEmpty(val)) {
                return "\"\"";
            } else if (val.IndexOfAny(new char[] { '\r', '\n', '\t', ' ', '#', '\'', '"', '{', '}', '=' }) == -1) {
                return val;
            } else {
                if (!val.Contains("\"") || val.Contains("\'")) {
                    return "\"" + val.Replace("\"", "\"\"") + "\"";
                } else {
                    return "\'" + val + "\'";
                }
            }
        }
    }

    public static class Deserializer
    {
        public static void LoadAppConfig<T>()
        {
            // Set properties from .conf file
            string assembyPath = Assembly.GetEntryAssembly().Location;
            string confPath = Path.Combine(Path.GetDirectoryName(assembyPath), Path.GetFileNameWithoutExtension(assembyPath) + ".conf");
            if (File.Exists(confPath)) {
                Deserialize(Parser.Parse(File.ReadAllText(confPath)), default(T));
            }

            // Set properties from command line
            List<Node> args = Parser.Parse(Environment.CommandLine);
            args.RemoveAt(0); // Skip the name of the executable
            Deserialize(args, default(T));
        }

        public static void Deserialize<T>(List<Node> from, T to)
        {
            foreach (Node node in from) {
                string name = node.Value.TrimStart('-');
                FieldInfo field = typeof(T).GetField(name, BindingFlags.Public | BindingFlags.Static);
                if (field != null) {
                    if (field.FieldType == typeof(TimeSpan)) {
                        TimeSpan val;
                        TimeSpan.TryParse(node.Children[0].Value, out val);
                        field.SetValue(null, val);
                    } else if (field.FieldType == typeof(bool) && (node.Children == null || node.Children.Count == 0)) {
                        field.SetValue(null, true);
                    } else {
                        object val = Convert.ChangeType(node.Children[0].Value, field.FieldType);
                        field.SetValue(null, val);
                    }
                } else {
                    StringBuilder sb = new StringBuilder();
                    foreach(FieldInfo fi in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static)) {
                        sb.Append(fi.Name);
                        sb.Append(" ");
                    }
                    throw new Exception("Unknown option: " + name + Environment.NewLine + "Valid options: " + sb.ToString());
                }
            }
        }
    }
}