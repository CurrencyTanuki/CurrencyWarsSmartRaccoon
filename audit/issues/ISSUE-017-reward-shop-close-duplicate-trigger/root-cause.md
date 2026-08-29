# ISSUE-017 confirmed root cause and change boundary

## Root-cause chain

1. `CloseShopAsync` confirms the entry page is `reward_shop` and clicks the shop toggle.
2. `WaitForPageAsync` requires two consecutive classifications of the expected preparation page.
3. Its deadline is checked only at the top of the loop. A synchronous OpenCV classification can finish after the deadline while contributing only the first matching observation.
4. `WaitForPageAsync` then returns false before it can collect the second observation.
5. `CloseShopAsync` immediately starts its next attempt and clicks the toggle again without first proving that the current page is still `reward_shop`.
6. A second toggle can reopen a shop that the first click already closed.

## Minimal intended boundary

Keep the existing maximum attempts and first-click behavior. After a failed verification, re-read a stable page before another click:

- expected preparation page: return success;
- confirmed `reward_shop`: a retry is allowed;
- null or any other page: stop safely without another input.

Do not change page templates, click coordinates, timeout values, OCR, general navigation, purchase planning, or unrelated reward-stage logic.

The deterministic before-red test recorded the two-input failure, and two
independent read-only reviewers confirmed the branch and the minimum change
boundary before the final regression run.

## Implemented change

Only `CloseShopAsync` changed. After `WaitForPageAsync` returns false, it now
calls the existing `ReadStablePageAsync` before another toggle input:

- expected preparation page: return success;
- confirmed `reward_shop`: publish the existing retry event and continue;
- null or another page: publish `CloseRewardShopRetryStopped`, return false,
  and send no additional input.

The global wait function, two-observation stability rule, five-second timeout,
templates, coordinates, OCR, purchase logic and the three-attempt ceiling were
not changed.
