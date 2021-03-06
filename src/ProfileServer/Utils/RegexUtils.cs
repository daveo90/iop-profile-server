﻿using ProfileServerProtocol;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ProfileServer.Utils
{
  /// <summary>
  /// Validates RegexType expression.
  /// </summary>
  public static class RegexTypeValidator
  {
    private static NLog.Logger log = NLog.LogManager.GetLogger("ProfileServer.Utils.RegexTypeValidator");


    /// <summary>List of characters that are allowed to be escaped in profile search regex or has special allowed meaning with backslash.</summary>
    private static HashSet<char> profileSearchRegexAllowedBackslashedCharacters = new HashSet<char>()
    {
      // Escaped chars
      'x', 'u', '.', '*', '.', '*', '-', '+', '[', ']', '\\', '?', '^', '|', '(', ')', '{', '}',

      // Special meaning chars
      'w', 'W', 's', 'S', 'd', 'D'
    };

    /// <summary>Regular expression to find forbidden substrings in profile search regular expression.</summary>
    private static Regex profileSearchRegexForbiddenSequenceRegex = new Regex(
       "(" + @"\{[^0-9]*\}" + ")" +     // Do not allow "{name}"
      "|(" + @"[\(\*\+\?\}]\?" + ")",   // Do not allow "(?", "*?", "+?", "??", "}?"
      RegexOptions.Singleline);



    /// <summary>
    /// Checks whether regular expression string has a valid RegexType format according to the protocol specification.
    /// </summary>
    /// <param name="RegexString">Regular expression string to check, must not be null.</param>
    /// <returns>true if the regular expression string is valid, false otherwise.</returns>
    /// <remarks>As the regular expression string is used as an input into System.Text.RegularExpressions.Regex.IsMatch 'input' parameter, 
    /// we are at risk that a malicious attacker submits an expression that supports wider spectrum of rules 
    /// than those required by the protocol, or that the constructed expression will take long time to execute and thus 
    /// it will perform a DoS attack. We eliminate the first problem by checking the actual format of the input regular expression 
    /// against a list of forbidden substrings and we eliminate the DoS problem by setting up a timeout on the total processing time over all search results.
    ///
    /// <para>See https://docs.microsoft.com/en-us/dotnet/articles/standard/base-types/quick-ref for .NET regular expression reference.</para>
    /// </remarks>
    public static bool ValidateProfileSearchRegex(string RegexString)
    {
      log.Trace("(RegexString:'{0}')", RegexString.SubstrMax());

      bool validLength = (Encoding.UTF8.GetByteCount(RegexString) <= ProtocolHelper.MaxProfileSearchExtraDataLengthBytes);
      bool validContent = true;
      if (validLength)
      {
        StringBuilder sb = new StringBuilder();

        // First, we remove all escaped characters out of the string.
        for (int i = 0; i < RegexString.Length; i++)
        {
          char c = RegexString[i];
          if (c == '\\')
          {
            if (i + 1 >= RegexString.Length)
            {
              log.Trace("Invalid backslash at the end of the regular expression.");
              validContent = false;
              break;
            }

            char cn = RegexString[i + 1];
            if (profileSearchRegexAllowedBackslashedCharacters.Contains(cn))
            {
              switch (cn)
              {
                case 'x':
                  // Potential \xnn sequence.
                  i += 2;
                  if (i + 2 >= RegexString.Length)
                  {
                    log.Trace("Invalid '\\x' sequence found at the end of the regular expression.");
                    validContent = false;
                    break;
                  }

                  if (RegexString[i].IsHexChar() && RegexString[i + 1].IsHexChar())
                  {
                    // Replace the sequence with verification neutral 'A' character.
                    sb.Append('A');
                    // Skip one digit, the second will be skipped by for loop.
                    i++;
                  }
                  else
                  {
                    log.Trace("Invalid '\\x' sequence found in the regular expression.");
                    validContent = false;
                  }
                  break;

                case 'u':
                  // Potential \unnnn sequence.
                  i += 2;
                  if (i + 4 >= RegexString.Length)
                  {
                    log.Trace("Invalid '\\u' sequence found at the end of the regular expression.");
                    validContent = false;
                    break;
                  }

                  if (RegexString[i].IsHexChar() && RegexString[i + 1].IsHexChar() && RegexString[i + 2].IsHexChar() && RegexString[i + 3].IsHexChar())
                  {
                    // Replace the sequence with verification neutral 'A' character.
                    sb.Append('A');
                    // Skip three digits, the fourth will be skipped by for loop.
                    i++;
                  }
                  else
                  {
                    log.Trace("Invalid '\\u' sequence found in the regular expression.");
                    validContent = false;
                  }
                  break;

                default:
                  // Replace the sequence with verification neutral 'A' character.
                  sb.Append('A');
                  // Character is allowed, skip it.
                  i++;
                  break;
              }
            }
            else
            {
              // Character is not allowed, this is an error.
              log.Trace("Invalid sequence '\\{0}' found in the regular expression.", cn);
              validContent = false;
              break;
            }
          }
          else
          {
            // Other chars just copy to the newly built string.
            sb.Append(c);
          }
        }


        string regexStr = sb.ToString();
        if (validContent)
        {
          // Now we have the string without any escaped characters, so we can run regular expression to find unallowed substrings.
          Match match = profileSearchRegexForbiddenSequenceRegex.Match(regexStr);
          if (match.Success)
          {
            log.Trace("Forbidden sequence '{0}' found in the regular expression.", match.Groups[1].Length > 0 ? match.Groups[1] : match.Groups[2]);
            validContent = false;
          }
        }
      }

      bool res = validLength && validContent;

      log.Trace("(-):{0}", res);
      return res;
    }
  }


  /// <summary>
  /// Regular expression evaluator with timeout feature.
  /// <para>
  /// It is used for regex matching of a large number of entities against a single pattern 
  /// while controling the time spent on the matching operations.
  /// </para>
  /// <para>
  /// There are two timeout values. The first one if for a single data matching operation, which should be a very small value - i.e. 100 ms.
  /// This prevents a single evaluation whether a certain input data matches the pattern or not to take too much time.
  /// The second timeout value is for overall time spent on matching with the particular object instance.
  /// Once this timeout is reached, the instance no longer performs any matching and just returns that data does not match the pattern.
  /// </para>
  /// </summary>
  public class RegexEval
  {
    private static NLog.Logger log = NLog.LogManager.GetLogger("ProfileServer.Utils.RegexEval");

    /// <summary>Regular expression object.</summary>
    private Regex regex;

    /// <summary>Stopwatch to measure execution time.</summary>
    private Stopwatch watch;

    /// <summary>Number of ticks there remains for matching operations.</summary>
    private long totalTimeRemainingTicks;

    /// <summary>
    /// Initializes the regular expression and stop watch.
    /// </summary>
    /// <param name="RegexStr">Regular expression.</param>
    /// <param name="SingleTimeoutMs">Timeout in milliseconds for a single data matching.</param>
    /// <param name="TotalTimeoutMs">Total timeout in milliseconds for the whole matching operation over the whole set of data.</param>
    public RegexEval(string RegexStr, int SingleTimeoutMs, int TotalTimeoutMs)
    {
      log.Trace("RegexStr:'{0}',SingleTimeoutMs:{1},TotalTimeoutMs:{2}", RegexStr.SubstrMax(), SingleTimeoutMs, TotalTimeoutMs);

      regex = new Regex(RegexStr, RegexOptions.Singleline, TimeSpan.FromMilliseconds(SingleTimeoutMs));
      watch = new Stopwatch();
      totalTimeRemainingTicks = TimeSpan.FromMilliseconds(TotalTimeoutMs).Ticks;

      log.Trace("(-)");
    }

    /// <summary>
    /// Checks whether a string matches a regular expression within a given time.
    /// </summary>
    /// <param name="Data">Input string to match.</param>
    /// <returns>true if the input <paramref name="Data"/> matches the given regular expression within the given time frame
    /// and if the total time for all matching operations with this instance was not reached, false otherwise.</returns>
    public bool Matches(string Data)
    {
      if (Data == null) Data = "";
      log.Trace("Data:'{0}'", Data.SubstrMax());

      bool res = false;
      string reason = "";
      if (totalTimeRemainingTicks > 0)
      {
        try
        {
          watch.Restart();

          res = regex.IsMatch(Data);

          watch.Stop();
          totalTimeRemainingTicks -= watch.ElapsedTicks;
          log.Trace("Total time remaining is {0} ticks.", totalTimeRemainingTicks);
        }
        catch
        {
          // Timeout occurred, no match.
          reason = "[TIMEOUT]";
        }
      }
      else
      {
        // No more time left for this instance, no match.
        reason = "[TOTAL_TIMEOUT]";
      }

      log.Trace("(-){0}:{1}", reason, res);
      return res;
    }

  }
}
