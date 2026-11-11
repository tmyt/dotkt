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
