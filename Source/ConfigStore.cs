﻿// Kerbal Development tools.
// Author: igor.zavoychinskiy@gmail.com
// This software is distributed under Public domain license.

using KSP.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace KSPDev.LocalizationTool {

static class ConfigStore {
  /// <summary>Writes the localization items into file.</summary>
  /// <remarks>
  /// <list type="bullet">
  /// <item>All items are grouped by <see cref="LocItem.groupKey"/>.</item>
  /// <item>The groups are sorted in the ascending order.</item>
  /// <item>The items within a group are sorted in the ascending order.</item>
  /// <item>
  /// If item has <see cref="LocItem.locDescription"/>, then its written as a comment that precedes
  /// the tag-to-value line.
  /// </item>
  /// </list>
  /// </remarks>
  /// <param name="items">The items to write.</param>
  /// <param name="lang">
  /// The language of the file. It will be used for the language block in the file.
  /// </param>
  /// <param name="filePath">The file path to write the data into.</param>
  public static void WriteLocItems(IEnumerable<LocItem> items, string lang, string filePath) {
    using (var file = new StreamWriter(filePath)) {
      file.WriteLine("// Auto generated by KSPDev Localization tool at: " + DateTime.Now);
      file.WriteLine("// Total strings: " + items.Count());
      file.WriteLine(
          "// Total words: " + items.Sum(x => Regex.Matches(x.locDefaultValue, @"\w+").Count));
      
      // Report duplicated tags if any.
      var duplicates = items
          .GroupBy(x => x.locTag)
          .Select(x => new { tag = x.Key, count = x.Count() })
          .Where(x => x.count > 1);
      foreach (var duplicate in duplicates) {
        file.WriteLine("// DUPLICATED TAG: " + duplicate.tag);
        Debug.LogWarningFormat("Found duplicated tag: {0}", duplicate.tag);
      }

      file.Write("Localization\n{\n\t" + lang + "\n\t{\n");
      var byGroupKey = items
          .OrderBy(x => x.groupKey)
          .ThenBy(x => x.sortKey)
          .ThenBy(x => x.locTag)
          .GroupBy(x => new { x.groupKey, x.sortKey });
      
      foreach (var groupKeyItems in byGroupKey) {
        var groupText = !string.IsNullOrEmpty(groupKeyItems.Key.sortKey)
            ? groupKeyItems.Key.groupKey + ", " + groupKeyItems.Key.sortKey
            : groupKeyItems.Key.groupKey;
        file.WriteLine("\n\t\t// ********** " + groupText + "\n");
        foreach (var item in groupKeyItems) {
          if (!string.IsNullOrEmpty(item.locDescription)) {
            file.WriteLine(MakeMultilineComment(2, item.locDescription, maxLineLength: 100 - 2*8));
          }
          if (!string.IsNullOrEmpty(item.locExample)) {
            file.WriteLine(MakeMultilineComment(2, "Example usage:"));
            file.WriteLine(MakeMultilineComment(2, item.locExample));
          }
          file.WriteLine(MakeConfigNodeLine(2, item.locTag, item.locDefaultValue));
        }
      }
      file.Write("\t}\n}\n");
    }
  }

  /// <summary>Loads a config file preserving the value comments.</summary>
  /// <remarks>
  /// <para>
  /// Unfortunately, the stock class can save the comments, but not loading them. This method loads
  /// a simple config file while keeping the comments. Note, that only a simple well formated config
  /// can be handled.
  /// </para>
  /// <para>
  /// The method keeps all the comments in the file including the empty lines which are treated as
  /// empty comments. When a comment is placed on the same line as a node or a value declaration,
  /// then it's added to the node or value. If the comment is placed on its own line, then it's
  /// added as a value of a special field named <c>__commentField</c>.   
  /// </para>
  /// </remarks>
  /// <param name="fileFullName">The file to load.</param>
  /// <param name="localizeValues">
  /// Tells if the field values should be localized. If not, then they are retruned from the file
  /// "as-is".
  /// </param>
  /// <returns>A loaded config node.</returns>
  public static ConfigNode LoadConfigWithComments(string fileFullName, bool localizeValues = true) {
    if (!File.Exists(fileFullName)) {
      return ConfigNode.Load(fileFullName);  // Just for the sake of the logs.
    }

    // $1 - key, $2 - other data
    var nodeMultiLinePrefixDeclRe = new Regex(@"^\s*([a-zA-Z_]+[a-zA-Z0-9_]*)$");
    // $1 - key, $2 - other data
    var nodeSameLineDeclRe = new Regex(@"^\s*([a-zA-Z_]+[a-zA-Z0-9_]*)\s*{\s*(.*?)?\s*$");
    // $1 - key, $2 - value
    var keyValueLineDeclRe = new Regex(@"^\s*([a-zA-Z_]+[a-zA-Z0-9_]*)\s*=\s*(.*?)$");

    var lines = File.ReadAllLines(fileFullName)
        .Select(x => x.Trim())
        .ToList();
    var nodesStack = new List<ConfigNode>() { new ConfigNode() };
    var node = nodesStack[0];
    var lineNum = 1;
    while (lines.Count > 0) {
      var line = lines[0];
      if (line.Length == 0) {
        lines.RemoveAt(0);
        lineNum++;
        node.AddValue("__commentField", "");
        continue;
      }
      
      // Check for the node section close.
      if (line.StartsWith("}", StringComparison.Ordinal)) {
        nodesStack.RemoveAt(nodesStack.Count - 1);
        if (nodesStack.Count == 0) {
          Debug.LogErrorFormat("Unexpected node close statement found at line {0} in {1}",
                               lineNum, fileFullName);
          return null;
        }
        node = nodesStack[nodesStack.Count - 1];
        if (line.Length == 1) {
          lines.RemoveAt(0);
          lineNum++;
        } else {
          lines[0] = line.Substring(1);
        }
        continue;
      }

      // Chop off the comment.
      string comment = null;
      var commentPos = line.IndexOf("//", StringComparison.Ordinal);
      if (commentPos != -1) {
        comment = line.Substring(commentPos + 2).TrimStart();
        line = line.Substring(0, commentPos).TrimEnd();
        if (line.Length == 0) {
          lines.RemoveAt(0);
          lineNum++;
          node.AddValue("__commentField", comment);
          continue;
        }
      }
      
      // Try handling the simples case: a key value pair (with an optional comment).
      var keyValueMatch = keyValueLineDeclRe.Match(line);
      if (keyValueMatch.Success) {
        // Localize the value if it starts from "#". There can be false positives.
        var value = keyValueMatch.Groups[2].Value;
        if (localizeValues && value.StartsWith("#", StringComparison.Ordinal)) {
          value = Localizer.Format(value);
        }
        node.AddValue(keyValueMatch.Groups[1].Value, value, comment);
        lines.RemoveAt(0);
        lineNum++;
        continue;
      }

      // Here it has to be a subnode. Check the otehr conditions it.
      string nodeName = null;
      string lineLeftOff = null;
      if (nodeSameLineDeclRe.IsMatch(line)) {
        // The node declaration starts on the same line. The can be more data in the same line!
        var sameLineMatch = nodeSameLineDeclRe.Match(line);
        nodeName = sameLineMatch.Groups[1].Value;
        lineLeftOff = sameLineMatch.Groups[2].Value;
        lines.RemoveAt(0);
        lineNum++;
      } else if (nodeMultiLinePrefixDeclRe.IsMatch(line)
                 && lines.Count > 0 && lines[1].StartsWith("{", StringComparison.Ordinal)) {
        // The node declaration starts on the next line.
        var multiLineMatch = nodeMultiLinePrefixDeclRe.Match(line);
        nodeName = multiLineMatch.Groups[1].Value;
        lines.RemoveAt(0);
        lineNum++;
        lineLeftOff = lines[0].Substring(1);  // Chop off "{"
        if (lineLeftOff.Length == 0) {
          lines.RemoveAt(0);
          lineNum++;
        } else {
          lines[0] = lineLeftOff;
        }
      }
      if (nodeName == null) {
        Debug.LogErrorFormat("Cannot parse node at line {0} in {1}", lineNum, fileFullName);
        return null;
      }
      var newNode = node.AddNode(nodeName, comment);
      nodesStack.Add(newNode);
      node = newNode;
    }
    
    return nodesStack[0];
  }

  /// <summary>Formats a comment with the proper indentation.</summary>
  /// <param name="indentation">The indentation in tabs. Each tab is 8 spaces.</param>
  /// <param name="comment">
  /// The comment to format. It can contain multiple lines separated by a "\n" symbols.
  /// </param>
  /// <param name="maxLineLength">
  /// A maximum length of the line in the file. If the comment exceeds this limit, then it's
  /// wrapped.
  /// </param>
  /// <returns>A properly formatted comment block.</returns>
  static string MakeMultilineComment(int indentation, string comment, int? maxLineLength = null) {
    return string.Join(
        "\n",
        comment.Split('\n')
            .SelectMany(l => WrapLine(l, maxLineLength - 3))  // -3 for the comment.
            .Select(x => new string('\t', indentation) + "// " + x).ToArray());
  }

  /// <summary>Wraps the line so that each item's length is not greater than the limit.</summary>
  /// <remarks>This method doesn't recognize any special symbols like tabs or line feeds.</remarks>
  /// <param name="line">The line to wrap.</param>
  /// <param name="maxLength">The maximum length of each item.</param>
  /// <returns>A list of line items.</returns>
  static List<string> WrapLine(string line, int? maxLength) {
    var lines = new List<string>();
    if (!maxLength.HasValue) {
      lines.Add(line);
      return lines;
    }
    var wordMatch = Regex.Match(line.Trim(), @"(\s*\S+\s*?)");
    var currentLine = "";
    while (wordMatch.Success) {
      if (currentLine.Length + wordMatch.Value.Length > maxLength) {
        lines.Add(currentLine);
        currentLine = wordMatch.Value.TrimStart();
      } else {
        currentLine += wordMatch.Value;
      }
      wordMatch = wordMatch.NextMatch();
    }
    if (currentLine.Length > 0) {
      lines.Add(currentLine);
    }
    return lines;
  }

  /// <summary>Formats a config node key/value line.</summary>
  /// <param name="indentation">The indentation in tabs. Each tab is 8 spaces.</param>
  /// <param name="key">The key string.</param>
  /// <param name="value">
  /// The value string. It can contain multiple lines separated by a "\n" symbols.
  /// </param>
  /// <returns>A properly formatted line.</returns>
  static string MakeConfigNodeLine(int indentation, string key, string value) {
    return new string('\t', indentation) + key + " = " + EscapeValue(value);
  }

  /// <summary>Escapes special symbols so that they don't break the formatting.</summary>
  /// <param name="value">The value to escape.</param>
  /// <returns>The escaped value.</returns>
  static string EscapeValue(string value) {
    // Turn the leading and the trailing spaces into the unicode codes. Othwerwise, they won't load.
    if (value.Length > 0) {
      value = EscapeChar(value[0]) + value.Substring(1);
    }
    if (value.Length > 1) {
      value = value.Substring(0, value.Length - 1) + EscapeChar(value[value.Length - 1]);
    }
    // Also, escape the linefeed character since it breaks the formatting.
    return value.Replace("\n", "\\n");
  }

  /// <summary>Escapes a whitespace character.</summary>
  /// <param name="c">The character to escape.</param>
  /// <returns>The unicode encode (\uXXXX) character string, or the character itself.</returns>
  static string EscapeChar(char c) {
    return c == ' ' || c == '\u00a0' || c == '\t'
        ? "\\u" + ((int)c).ToString("x4")
        : "" + c;
  }
}

}  // namesapce
