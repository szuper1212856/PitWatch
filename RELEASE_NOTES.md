## Voice input now works reliably

Voice questions could hang for 45 seconds and then fail with a misleading
"network problem" message. The request was stalling on a connection handshake
that never completed - nothing to do with your connection or your API key.

## Better handling of Google's limits

Google's free tier has a daily request limit that varies a lot between models,
and some of the newer ones allow as few as 20 requests per day. PitWatch now
tells you exactly what Google said: which limit you hit, which model it applies
to, and when it resets - instead of a vague "rate limited" message.

There are also fewer automatic retries, because each retry spends another
request from that daily allowance.

## Pick your own model

Settings can now fetch the list of models your API key can actually use. If one
is overloaded or out of quota, switch to another - both limits are per model, so
switching usually fixes it straight away. Lighter "flash-lite" models generally
have much larger daily allowances.

## Fixes

  * The position readout showed a nonsense car count, like "P1 of 1044357427"
  * Lap position was being read incorrectly, which also affected sector analysis
    and the coaching that tells you where you're losing time
  * Testing your API key no longer times out on a valid key
