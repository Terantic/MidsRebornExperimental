using Jace;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using FastDeepCloner;
using Mids_Reborn.Core.Base.Master_Classes;

namespace Mids_Reborn.Core
{
    public static class BooleanExprPreprocessor
    {
        /// <summary>
        /// Get config value from the ConfigData.CombatContextSettings branch
        /// </summary>
        /// <param name="cond">Config field name</param>
        /// <param name="stringFormat">Use string output if true, int if false</param>
        /// <returns>Value of config field according to format</returns>
        private static dynamic GetConfigValue(string cond, bool stringFormat = true)
        {
            var chunks = cond.ToLowerInvariant().Split('.');
            if (chunks.Length != 3)
            {
                return stringFormat ? "0" : 0;
            }

            var group = MidsContext.Config.CombatContextSettings.GetType()
                .GetProperty(ConfigData.CombatContext.GetConfigChunkName(chunks[1]));

            if (group == null)
            {
                return stringFormat ? "0" : 0;
            }

            var v = group
                .GetValue(MidsContext.Config.CombatContextSettings)?
                .GetType()
                .GetProperty(ConfigData.CombatContext.GetConfigChunkName(chunks[2]))?
                .GetValue(group.GetValue(MidsContext.Config.CombatContextSettings));
            var condType = ConfigData.CombatContext.ConfigChunkType(chunks[2]);

            return stringFormat switch
            {
                true => condType switch
                {
                    "bool" => v as bool? ?? true ? "1" : "0",
                    "int" => $"{v as int? ?? 100}",
                    _ => throw new ArgumentOutOfRangeException(nameof(condType))
                },
                _ => condType switch
                {
                    "bool" => v as bool? ?? true ? 1 : 0,
                    "int" => v as int? ?? 100,
                    _ => throw new ArgumentOutOfRangeException(nameof(condType))
                }
            };
        }

        /// <summary>
        /// Stack count a Stacks conditional compares against: the current build's entry for the power when it has one,
        /// since the shared database power keeps whatever value an earlier build or UI change gave it.
        /// </summary>
        /// <param name="conditionPower">Power named by the conditional</param>
        /// <returns>Stack count</returns>
        public static int GetConditionStacks(IPower conditionPower)
        {
            var pe = MidsContext.Character?.CurrentBuild?.Powers
                .FirstOrDefault(e => e?.Power != null && e.Power.StaticIndex == conditionPower.StaticIndex);

            return pe?.VariableValue ?? conditionPower.Stacks;
        }

        /// <summary>
        /// Validate a single Config conditional against the combat context settings
        /// </summary>
        /// <param name="settingName">Config field name, e.g. cfg.PlayerSettings.hppercent or cfg.player.isalive</param>
        /// <param name="conditionValue">Conditional value: operator and operand, e.g. "&lt; 76" or "= True"</param>
        /// <returns>True if the current setting satisfies the conditional</returns>
        public static bool ValidateConfigCondition(string settingName, string conditionValue)
        {
            var cVal = conditionValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cVal.Length < 2)
            {
                return false;
            }

            var chunks = settingName.ToLowerInvariant().Split('.');
            if (ConfigData.CombatContext.ConfigChunkType(chunks[^1]) == "bool")
            {
                if (!bool.TryParse(cVal[1], out var expected))
                {
                    return false;
                }

                bool current = GetConfigValue(settingName, false) > 0;

                return cVal[0] switch
                {
                    "=" => current == expected,
                    _ => false
                };
            }

            if (!int.TryParse(cVal[1], out var operand))
            {
                return false;
            }

            int value = GetConfigValue(settingName, false);

            return cVal[0] switch
            {
                "=" => value == operand,
                ">" => value > operand,
                "<" => value < operand,
                _ => false
            };
        }

        /// <summary>
        /// Build global infix expression from conditionals, restrict to conditional type and power name
        /// </summary>
        /// <param name="effect">Source effect</param>
        /// <param name="cType">Conditional type</param>
        /// <param name="cPowerName">Conditional power name</param>
        /// <returns>Single expression string with infix operators</returns>
        private static string BuildGlobalExpression(IEffect effect, string cType, string cPowerName)
        {
            if (effect.ActiveConditionals is not {Count: > 0})
            {
                return "0";
            }

            var getCondition = new Regex("(:.*)");
            var getConditionItem = new Regex("(.*:)");
            var conditionResults = new List<bool>();
            var conditionTypes = new List<string>();

            foreach (var cVp in effect.ActiveConditionals)
            {
                conditionTypes.Add(cVp.Key.Contains("OR ") ? "OR" : "AND");

                var k = cVp.Key.Replace("AND ", "").Replace("OR ", "");
                var condition = getCondition.Replace(k, "");
                var conditionItemName = getConditionItem.Replace(k, "").Replace(":", "");
                var conditionPower = condition == "Config" ? null : DatabaseAPI.GetPowerByFullName(conditionItemName);
                var cVal = cVp.Value.Split(' ');
                var powerDisplayName = conditionPower?.DisplayName;

                if (condition != "Config")
                {
                    if (powerDisplayName == null || !powerDisplayName.Contains(cPowerName))
                    {
                        return "0";
                    }
                }

                if (string.Equals(cType, condition, StringComparison.CurrentCultureIgnoreCase) && condition == "Active")
                {
                    bool? boolVal = Convert.ToBoolean(cVp.Value);
                    conditionResults.Add(MidsContext.Character.CurrentBuild.PowerActive(conditionPower) == boolVal);
                }
                else if (string.Equals(cType, condition, StringComparison.CurrentCultureIgnoreCase) && condition == "Taken")
                {
                    conditionResults.Add(MidsContext.Character.CurrentBuild.PowerUsed(conditionPower).Equals(Convert.ToBoolean(cVp.Value)));
                }
                else if (string.Equals(cType, condition, StringComparison.CurrentCultureIgnoreCase) && condition == "Stacks")
                {
                    conditionResults.Add(cVal[0] switch
                    {
                        "=" => GetConditionStacks(conditionPower).Equals(Convert.ToInt32(cVal[1])),
                        ">" => GetConditionStacks(conditionPower) > Convert.ToInt32(cVal[1]),
                        "<" => GetConditionStacks(conditionPower) < Convert.ToInt32(cVal[1]),
                        _ => true
                    });
                }
                else if (string.Equals(cType, condition, StringComparison.CurrentCultureIgnoreCase) && condition == "Team")
                {
                    conditionResults.Add(cVal[0] switch
                    {
                        "=" => MidsContext.Config.TeamMembers.ContainsKey(conditionItemName) && MidsContext.Config
                            .TeamMembers[conditionItemName]
                            .Equals(Convert.ToInt32(cVal[1])),
                        ">" => MidsContext.Config.TeamMembers.ContainsKey(conditionItemName) &&
                               MidsContext.Config.TeamMembers[conditionItemName] > Convert.ToInt32(cVal[1]),
                        "<" => MidsContext.Config.TeamMembers.ContainsKey(conditionItemName) &&
                               MidsContext.Config.TeamMembers[conditionItemName] < Convert.ToInt32(cVal[1]),
                        _ => true
                    });
                }
                else if (string.Equals(cType, condition, StringComparison.CurrentCultureIgnoreCase) && condition == "Config")
                {
                    conditionResults.Add(ValidateConfigCondition(conditionItemName, cVp.Value));
                }
                else
                {
                    conditionResults.Add(true);
                }
            }

            var expr = "";

            for (var i = 0; i < conditionResults.Count; i++)
            {
                expr += i == 0
                    ? conditionResults[i] ? "1" : "0"
                    : $" {conditionTypes[i]} {(conditionResults[i] ? "1" : "0")}";
            }

            return expr;
        }

        /// <summary>
        /// Build global infix expression from conditionals, restrict to power name
        /// </summary>
        /// <param name="effect">Source effect</param>
        /// <param name="cPowerName">Conditional power name</param>
        /// <returns>Single expression string with infix operators</returns>
        private static string BuildGlobalExpression(IEffect effect, string cPowerName)
        {
            if (effect.ActiveConditionals is not { Count: > 0 })
            {
                return "0";
            }

            var getCondition = new Regex("(:.*)");
            var getConditionItem = new Regex("(.*:)");
            var conditionResults = new List<bool>();
            var conditionTypes = new List<string>();
            foreach (var cVp in effect.ActiveConditionals)
            {
                conditionTypes.Add(cVp.Key.Contains("OR ") ? "OR" : "AND");

                var k = cVp.Key.Replace("AND ", "").Replace("OR ", "");
                var condition = getCondition.Replace(k, "");
                var conditionItemName = getConditionItem.Replace(k, "").Replace(":", "");
                var conditionPower = condition == "Config" ? null : DatabaseAPI.GetPowerByFullName(conditionItemName);
                var buildPowers = MidsContext.Character.CurrentBuild.Powers;
                var cVal = cVp.Value.Split(' ');
                var powerDisplayName = conditionPower?.DisplayName;
                if (condition != "Config")
                {
                    if (powerDisplayName == null || !powerDisplayName.Contains(cPowerName))
                    {
                        return "0";
                    }
                }

                switch (condition)
                {
                    case "Active":
                        bool? boolVal = Convert.ToBoolean(cVp.Value);
                        conditionResults.Add(MidsContext.Character.CurrentBuild.PowerActive(conditionPower) == boolVal);

                        break;
                    case "Taken":
                        conditionResults.Add(MidsContext.Character.CurrentBuild.PowerUsed(conditionPower)
                            .Equals(Convert.ToBoolean(cVp.Value)));

                        break;
                    case "Stacks":
                        var stacks = buildPowers
                            .Where(x => x != null && x.Power == conditionPower)
                            .Select(x => x.VariableValue)
                            .ToList();
                        conditionResults.Add(cVal[0] switch
                        {
                            "=" => stacks[0].Equals(Convert.ToInt32(cVal[1])),
                            ">" => stacks[0] > Convert.ToInt32(cVal[1]),
                            "<" => stacks[0] < Convert.ToInt32(cVal[1]),
                            _ => true
                        });

                        break;
                    case "Team":
                        conditionResults.Add(cVal[0] switch
                        {
                            "=" => MidsContext.Config.TeamMembers.ContainsKey(conditionItemName) && MidsContext.Config
                                .TeamMembers[conditionItemName]
                                .Equals(Convert.ToInt32(cVal[1])),
                            ">" => MidsContext.Config.TeamMembers.ContainsKey(conditionItemName) &&
                                   MidsContext.Config.TeamMembers[conditionItemName] > Convert.ToInt32(cVal[1]),
                            "<" => MidsContext.Config.TeamMembers.ContainsKey(conditionItemName) &&
                                   MidsContext.Config.TeamMembers[conditionItemName] < Convert.ToInt32(cVal[1]),
                            _ => true
                        });

                        break;

                    case "Config":
                        conditionResults.Add(ValidateConfigCondition(conditionItemName, cVp.Value));

                        break;

                    default:
                        conditionResults.Add(true);
                        break;
                }
            }

            var expr = "";

            for (var i = 0; i < conditionResults.Count; i++)
            {
                expr += i == 0
                    ? conditionResults[i] ? "1" : "0"
                    : $" {conditionTypes[i]} {(conditionResults[i] ? "1" : "0")}";
            }

            return expr;
        }

        /// <summary>
        /// Split expression into tokens, by operator (either AND or OR)
        /// </summary>
        /// <param name="expr">Global infix expression</param>
        /// <returns>List of expression tokens (values/operators)</returns>
        private static List<string> Tokenize(string expr)
        {
            return Regex.Split(expr, @" (AND|OR) ").ToList();
        }

        /// <summary>
        /// Transforms and merge infix operators of the selected kind into prefix functions style.
        /// Due to limitations of the math engine, operators are limited to two members (func(a, b))
        /// </summary>
        /// <param name="exprTokens">Expression tokens - <see cref="Tokenize"/></param>
        /// <param name="op">Operator to look for (either AND or OR)</param>
        /// <returns>List of prefix functions tokens</returns>
        private static List<string> Merge(List<string> exprTokens, string op)
        {
            // Work on a local copy...
            // Obsolete ?
            var mergedTokens = exprTokens.Clone();
            var m = true;
            var n = 0;
            var l = mergedTokens.Count;
            while (m & n++ < 30)
            {
                var lastPos = -1;
                var tokensPos = new Dictionary<int, List<int>>(); // Position of tokens, positions of members
                for (var i = 0; i < mergedTokens.Count; i++)
                {
                    if (mergedTokens[i] != op)
                    {
                        continue;
                    }

                    if (lastPos == -1 || i - lastPos > 1)
                    {
                        lastPos = i;
                        tokensPos.Add(i, new List<int> {i - 1, i + 1});
                    }
                }

                // No more matching operators
                if (tokensPos.Count <= 0)
                {
                    return mergedTokens;
                }

                // Process only first operator found
                var t = tokensPos.First();
                if (t.Value[0] < mergedTokens.Count && t.Value[1] < mergedTokens.Count)
                {
                    mergedTokens[t.Key] = $"{op}({string.Join(", ", t.Value.Select(e => mergedTokens[e]))})";
                    
                    // Remove values from t.Value in one pass
                    // More robust than two List.RemoveAt()
                    mergedTokens = mergedTokens
                        .Select((e, i) => new KeyValuePair<int, string>(i, e))
                        .Where(e => !t.Value.Contains(e.Key))
                        .Select(e => e.Value)
                        .ToList();

                    m = l != mergedTokens.Count;
                    l = mergedTokens.Count;
                }
                else
                {
                    m = false;
                }
            }

            return mergedTokens;
        }

        private static string Build(string expr)
        {
            var tokens = Tokenize(expr);
            tokens = Merge(tokens, "AND");
            tokens = Merge(tokens, "OR");
            
            return string.Join(" ", tokens);
        }

        /// <summary>
        /// Validate all conditionals of an effect, as an AND/OR chain (AND binds tighter than OR)
        /// </summary>
        /// <param name="effect">Source effect</param>
        /// <returns>Result of expression, as boolean</returns>
        public static bool Parse(IEffect effect)
        {
            if (effect.ActiveConditionals is not {Count: > 0})
            {
                return true;
            }

            // Evaluated directly rather than through the math engine: this is called for every
            // conditional effect on each recalculation (CanInclude/CanGrantPower).
            // Every conditional is validated, as ValidateConditional(int) also refreshes its Validated flag.
            var result = false;
            var andGroup = true;
            for (var i = 0; i < effect.ActiveConditionals.Count; i++)
            {
                var valid = effect.ValidateConditional(i);
                if (i > 0 && effect.ActiveConditionals[i].Key.StartsWith("OR "))
                {
                    result |= andGroup;
                    andGroup = valid;
                }
                else
                {
                    andGroup &= valid;
                }
            }

            return result | andGroup;
        }

        /// <summary>
        /// Compute prefix style formula
        /// </summary>
        /// <param name="effect">Source effect</param>
        /// <param name="cType">Conditional type</param>
        /// <param name="cPowerName">Conditional power name</param>
        /// <seealso cref="BuildGlobalExpression(IEffect, string, string)"/>
        /// <seealso cref="Build(string)"/>
        /// <returns>Result of expression, as boolean</returns>
        public static bool Parse(IEffect effect, string cType, string cPowerName)
        {
            if (effect.ActiveConditionals is {Count: <= 0})
            {
                return false;
            }

            var expr = BuildGlobalExpression(effect, cType, cPowerName);
            var prefixExpr = Build(expr);
            var mathEngine = CalculationEngine.New<double>();

            mathEngine.AddFunction("AND", (a, b) => a > 0 & b > 0 ? 1 : 0);
            mathEngine.AddFunction("OR", (a, b) => a > 0 | b > 0 ? 1 : 0);

            if (string.IsNullOrWhiteSpace(prefixExpr))
            {
                return true;
            }

            var ret = false;
            try
            {
                ret = mathEngine.Calculate(prefixExpr) > 0;
            }
            catch (ParseException ex)
            {
                Debug.WriteLine($"Conditional check failed in {prefixExpr}\nPower: {effect.GetPower()?.FullName}\r\n{ex.Message}");

                return false;
            }
            catch (VariableNotDefinedException ex)
            {
                Debug.WriteLine($"Conditional check failed (variable not defined) in {prefixExpr}\nPower: {effect.GetPower()?.FullName}\r\n{ex.Message}");

                return false;
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"Conditional check failed (invalid operation) in {prefixExpr}\nPower: {effect.GetPower()?.FullName}\r\n{ex.Message}");

                return false;
            }

            return ret;
        }

        /// <summary>
        /// Compute prefix style formula
        /// </summary>
        /// <param name="effect">Source effect</param>
        /// <param name="cPowerName">Conditional power name</param>
        /// <seealso cref="BuildGlobalExpression(IEffect, string)"/>
        /// <seealso cref="Build(string)"/>
        /// <returns>Result of expression, as boolean</returns>
        public static bool Parse(IEffect effect, string cPowerName)
        {
            if (effect.ActiveConditionals is { Count: <= 0 })
            {
                return false;
            }

            var expr = BuildGlobalExpression(effect, cPowerName);
            var prefixExpr = Build(expr);
            var mathEngine = CalculationEngine.New<double>();

            mathEngine.AddFunction("AND", (a, b) => a > 0 & b > 0 ? 1 : 0);
            mathEngine.AddFunction("OR", (a, b) => a > 0 | b > 0 ? 1 : 0);

            if (string.IsNullOrWhiteSpace(prefixExpr))
            {
                return true;
            }

            var ret = false;
            try
            {
                ret = mathEngine.Calculate(prefixExpr) > 0;
            }
            catch (ParseException ex)
            {
                Debug.WriteLine($"Conditional check failed in {prefixExpr}\nPower: {effect.GetPower()?.FullName}\r\n{ex.Message}");

                return false;
            }
            catch (VariableNotDefinedException ex)
            {
                Debug.WriteLine($"Conditional check failed (variable not defined) in {prefixExpr}\nPower: {effect.GetPower()?.FullName}\r\n{ex.Message}");

                return false;
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"Conditional check failed (invalid operation) in {prefixExpr}\nPower: {effect.GetPower()?.FullName}\r\n{ex.Message}");

                return false;
            }

            return ret;
        }
    }
}
