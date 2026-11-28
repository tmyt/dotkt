# Assertions describe semantic children, not JSON property order. Complete arrays retain exact length/order;
# {"$prefix":[...]} describes an operand-vector prefix; {"$any":true} requires a field's presence only.
def ir_matches($pattern):
  . as $actual |
  if ($pattern | type) == "object" then
    if $pattern == {"$any":true} then true
    elif ($pattern | keys) == ["$prefix"] then
      ($actual | type) == "array" and ($actual | length) >= ($pattern["$prefix"] | length)
      and all(range(0; $pattern["$prefix"] | length); . as $i | $actual[$i] | ir_matches($pattern["$prefix"][$i]))
    else
      ($actual | type) == "object"
      and all($pattern | keys[]; . as $key | ($actual | has($key)) and ($actual[$key] | ir_matches($pattern[$key])))
    end
  elif ($pattern | type) == "array" then
    ($actual | type) == "array" and ($actual | length) == ($pattern | length)
    and all(range(0; $pattern | length); . as $i | $actual[$i] | ir_matches($pattern[$i]))
  else $actual == $pattern
  end;

# Inspect the selected call with its actual staged operand initializers. Limit
# substitution to immutable temporaries declared in this very value block.
def ir_call_operands:
  if .k == "callInstance" then .
  elif .k == "valueBlock" and .result.k == "callInstance" then
    . as $block | INDEX(.stmts[] | select(.k=="var" and has("init")); .name) as $locals |
    def operand: if .k=="local" and $locals[.name] != null and
      (.name as $n | all($block.stmts[] | .. | objects; .k!="setLocal" or .name!=$n))
      then $locals[.name].init else . end;
    .result | .recv |= operand | .args |= map(operand)
  else empty end;
