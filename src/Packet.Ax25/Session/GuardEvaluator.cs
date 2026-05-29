namespace Packet.Ax25.Session;

/// <summary>
/// Evaluates the small boolean expression language used in SDL transition
/// <c>guard:</c> fields. Grammar:
/// <code>
///   expr   := term ("or" term)*
///   term   := factor ("and" factor)*
///   factor := "not"? identifier
/// </code>
/// Identifiers are arbitrary names resolved against an externally-supplied
/// binding table (e.g. <c>"own_receiver_busy"</c> → a lambda reading
/// <c>ctx.OwnReceiverBusy</c>).
/// </summary>
/// <remarks>
/// Whitespace separates tokens. Parentheses are not supported — every
/// guard observed so far in the spec is a simple conjunction of negated
/// or unnegated flags, occasionally an <c>or</c>. We extend the grammar
/// only as new SDL pages need it.
/// </remarks>
public sealed class GuardEvaluator
{
    private static readonly char[] TokenSeparators = { ' ', '\t' };

    private readonly IReadOnlyDictionary<string, Func<bool>> bindings;

    /// <summary>
    /// Build an evaluator over <paramref name="bindings"/>. Each entry maps
    /// an identifier name to a thunk that returns the identifier's current
    /// boolean value. Thunks are evaluated at each <see cref="Evaluate"/>
    /// call, so capturing mutable state via closure is the normal pattern.
    /// </summary>
    public GuardEvaluator(IReadOnlyDictionary<string, Func<bool>> bindings)
    {
        this.bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
    }

    /// <summary>
    /// Evaluate <paramref name="expression"/>. Returns <c>true</c> if the
    /// guard holds. Empty / null / whitespace-only expression is treated as
    /// <c>true</c> (no guard).
    /// </summary>
    /// <exception cref="GuardEvaluationException">
    /// Thrown when the expression references an unbound identifier, or has
    /// a syntax error.
    /// </exception>
    public bool Evaluate(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        var tokens = expression.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
        int index = 0;
        bool result = ParseOr(tokens, ref index, expression);
        if (index != tokens.Length)
        {
            throw new GuardEvaluationException($"trailing tokens in guard expression '{expression}' at position {index}");
        }
        return result;
    }

    private bool ParseOr(string[] tokens, ref int idx, string original)
    {
        var result = ParseAnd(tokens, ref idx, original);
        while (idx < tokens.Length && tokens[idx] == "or")
        {
            idx++;
            var right = ParseAnd(tokens, ref idx, original);
            result = result || right;
        }
        return result;
    }

    private bool ParseAnd(string[] tokens, ref int idx, string original)
    {
        var result = ParseFactor(tokens, ref idx, original);
        while (idx < tokens.Length && tokens[idx] == "and")
        {
            idx++;
            var right = ParseFactor(tokens, ref idx, original);
            result = result && right;
        }
        return result;
    }

    private bool ParseFactor(string[] tokens, ref int idx, string original)
    {
        if (idx >= tokens.Length)
        {
            throw new GuardEvaluationException($"expected identifier in '{original}' at position {idx}");
        }
        bool negate = false;
        if (tokens[idx] == "not")
        {
            negate = true;
            idx++;
            if (idx >= tokens.Length)
            {
                throw new GuardEvaluationException($"expected identifier after 'not' in '{original}'");
            }
        }
        var ident = tokens[idx++];
        if (!bindings.TryGetValue(ident, out var pred))
        {
            // The codegen has historically emitted predicate names in two
            // shapes: the early hand-authored style (`V_s_eq_V_a`,
            // `acknowledge_pending`, `srej_enabled`) and the post-walker-
            // normalisation style introduced in Packet.Ax25.Sdl v0.5.0
            // (`vs_eq_va`, `ack_pending`, `SREJ_enabled`). Bindings get
            // registered under the historic spelling; when a guard
            // references the new spelling, fall back to the canonical name.
            // If a caller has overridden the canonical binding (typical in
            // smoke tests), that override is preserved — the alias resolves
            // at evaluation time against whatever's in the dictionary now.
            if (PredicateAliases.TryGetValue(ident, out var canonical)
                && bindings.TryGetValue(canonical, out pred))
            {
                // matched via alias; fall through
            }
            else
            {
                throw new GuardEvaluationException($"unbound identifier '{ident}' in '{original}' — add a binding before evaluating this guard");
            }
        }
        var value = pred();
        return negate ? !value : value;
    }

    /// <summary>
    /// Maps post-walker-normalisation predicate names (the spelling
    /// <c>Packet.Ax25.Sdl</c> v0.5.0+ emits after operator-character
    /// rewriting) to the historic canonical binding name registered by
    /// <see cref="Ax25SessionBindings.CreateDefault"/>. Used as a fallback
    /// when the literal name isn't in the bindings dictionary, so guards
    /// from regenerated yamls resolve against the same closures the
    /// original code expected — including any test-time overrides.
    /// </summary>
    private static readonly Dictionary<string, string> PredicateAliases =
        new(StringComparer.Ordinal)
        {
            // Sequence-variable comparisons — post-normalisation drops the
            // V_s / V_a / N_r etc. underscores and lowercases the lot.
            ["vs_eq_va"]                                      = "V_s_eq_V_a",
            ["vs_eq_va_plus_k"]                               = "V_s_eq_V_a_plus_k",
            ["vs_eq_X"]                                       = "v_s_eq_x",
            ["ns_eq_vr"]                                      = "N_s_eq_V_r",
            ["ns_gt_vr_plus_1"]                               = "N_s_gt_V_r_plus_1",
            ["va_le_nr_le_vs"]                                = "V_a_le_N_r_le_V_s",
            ["nr_eq_vs"]                                      = "n_r_eq_v_s",
            // figc4.5 recovery-complete after "V(a) := N(r)": ax25sdl#53 emits
            // the post-assignment guard `vs_eq_nr` (V(s) == N(r)), which is the
            // same comparison as `nr_eq_vs`.
            ["vs_eq_nr"]                                      = "n_r_eq_v_s",
            ["nr_eq_va"]                                      = "n_r_eq_v_a",
            // figc4.4 / figc4.5 stored-frame drain loop predicate. The
            // package preserves the spec variable `I`'s capital (V(r) I Frame
            // Stored?), so it emits `vr_I_frame_stored`; the binding is
            // registered lower-case.
            ["vr_I_frame_stored"]                            = "vr_i_frame_stored",

            // Compound flags — operator-character substitution turns `&` /
            // `||` into `_and_` / `_or_` and preserves spec-variable casing
            // (so `P` and `F` stay capital).
            ["command_and_P_eq_1"]                                              = "command_and_p_eq_1",
            ["response_and_F_eq_1"]                                             = "response_and_f_eq_1",
            ["F_eq_1_and_frame_eq_RR_or_frame_eq_RNR_or_frame_eq_I"]            = "f_eq_1_and_supervisory_or_i",
            ["info_field_length_le_N1_and_content_is_octet_aligned"]            = "info_field_valid",

            // Flag aliases.
            ["peer_busy"]                                     = "peer_receiver_busy",
            ["own_receive_busy"]                              = "own_receiver_busy",   // SDL typo: missing 'r'
            ["ACK_pending"]                                   = "acknowledge_pending",
            ["ack_pending"]                                   = "acknowledge_pending",
            ["RC_eq_0"]                                       = "rc_eq_0",
            ["T1_expired"]                                    = "t1_expired",
            ["SREJ_enabled"]                                  = "srej_enabled",
            ["sreject_exception_gt_0"]                        = "srej_exception_gt_0",
        };
}

/// <summary>Thrown when a guard expression is malformed or references unbound identifiers.</summary>
public sealed class GuardEvaluationException : Exception
{
    public GuardEvaluationException(string message) : base(message) { }
}
