# WebSocket Adapter Server — Flow & Events

This document describes the WebSocket server that acts as an **adapter** between an **EGM** (Electronic Gaming Machine) and a **Roulette** game client. It explains the message flow, all events, and how a user puts money in, places bets, and cashes out.

---

## 1. Overview

- **Endpoint:** `ws://localhost:5000/ws`
- **Role:** Translate and route messages between two client types; the server does **not** run game logic.
- **Clients:**
  - **EGM** — machine/backend that handles cash (bills, AFT), credits, and game result display.
  - **Roulette** — game logic/UI that runs rounds, accepts bets, and produces results.

The server identifies each WebSocket as **EGM** or **ROULETTE** from the first message it receives (by event type), then routes and translates messages accordingly.

---

## 2. High-Level Flow

```
                    ┌─────────────────────────────────────────────────────────┐
                    │              WebSocket Adapter Server                    │
                    │                  (ws://localhost:5000/ws)                 │
                    └─────────────────────────────────────────────────────────┘
                                          │
           ┌──────────────────────────────┼──────────────────────────────┐
           │                              │                              │
           ▼                              │                              ▼
   ┌───────────────┐                      │                      ┌───────────────┐
   │     EGM       │  ◄─── GAME_UPDATE    │    session_initialized ───►  │   ROULETTE   │
   │  (machine)    │                      │    cash_event                │   (game UI)  │
   │               │  ───► BILL_INSERTED   │    aft_transfer              │               │
   │               │  ───► AFT_DEPOSIT     │    balance_snapshot          │  ◄── bet_commit
   │               │  ───► AFT_CASHOUT     │    cashout_ack               │  ───► round_result
   │               │  ───► ui_ping         │    bet_commit_ack             │  ◄── round_state
   │               │  ───► SPIN_COMPLETED  │    ui_ping, SPIN_COMPLETED    │  ───► round_summary
   └───────────────┘                      │                              │  ───► refund, etc.
                                         │                              └───────────────┘
```

- **EGM → Server:** Cash/credit events and UI sync (e.g. `BILL_INSERTED`, `AFT_DEPOSIT`, `AFT_CASHOUT`, `ui_ping`, `SPIN_COMPLETED`).
- **Server → Roulette:** Session, credits, balance, bet ack, and pass-throughs (`session_initialized`, `cash_event`, `aft_transfer`, `balance_snapshot`, `bet_commit_ack`, `ui_ping`, `SPIN_COMPLETED`, `cashout_ack`).
- **Roulette → Server:** Bet and round lifecycle (`bet_commit`, `round_result`, `round_state`, `round_summary`, `refund`, `round_void`, `error`, `ui_pong`, `cashout`, `AFT_CONFIRMED`).
- **Server → EGM:** Only **GAME_UPDATE** (bet/win result after a round).

---

## 3. All Events Reference

Event names are read from `EventType`, `eventType`, or `event` in the JSON root.

### 3.1 EGM → Server (inbound)

| Event             | Purpose |
|-------------------|--------|
| `BILL_INSERTED`  | User inserted cash; EGM sends amount and current credits. |
| `AFT_DEPOSIT`     | Account Fund Transfer deposit; EGM sends amount, `CurrentCredits`, optional `AFTReference`. |
| `AFT_CASHOUT`     | User cashed out; EGM sends amount, optional bet/win, balance goes to 0. |
| `ui_ping` / `UI_PING` | Keep-alive / sync; can also identify EGM on first message. |
| `SPIN_COMPLETED`  | EGM reports spin animation finished; forwarded to Roulette. |
| `CONNECTION_TEST` | Not used by EGM; EGM identifies via **session_initialized** only. |
| `session_initialized` | EGM sends once at startup with `client: "EGM_Application"` and `payload.availableCredits`. Server forwards it to Roulette once (this is the only trigger for sending session_initialized to Roulette from EGM). |

### 3.2 Server → Roulette (outbound)

| Event                 | When sent | Purpose |
|-----------------------|-----------|--------|
| `session_initialized` | Only when EGM sends `session_initialized` once at startup (with `client: "EGM_Application"`). Never sent when Roulette connects/reconnects. | Session start; includes `payload.availableCredits` from EGM (and egmId, jurisdiction, currency, etc.). |
| `cash_event`          | After `BILL_INSERTED` | Cash-in translated to Roulette format (`type: "bv_stack"`, denomination, meters, etc.). |
| `aft_transfer`        | After `AFT_DEPOSIT`   | AFT deposit (amount, authCode, remainingBalance, etc.). |
| `balance_snapshot`    | After `BILL_INSERTED`, `AFT_DEPOSIT`, or `AFT_CASHOUT` | Current credits for Roulette (`payload.availableCredits`; 0 after cashout). |
| `cashout_ack`         | After `AFT_CASHOUT`   | Cashout acknowledged (roundId, accepted, egmBalanceBefore/After, bet, win). |
| `bet_commit_ack`      | After Roulette sends `bet_commit` with valid stake | Bet accepted; includes roundId, egmBalanceBefore/After, meters (coinsIn, gamesPlayed). |
| `ui_ping`             | After EGM sends `ui_ping` / `UI_PING` | Pass-through to Roulette (same event name, translated payload). |
| `SPIN_COMPLETED`      | After EGM sends `SPIN_COMPLETED` | Pass-through so Roulette knows spin animation finished. |

### 3.3 Roulette → Server (inbound)

| Event           | Purpose |
|-----------------|--------|
| `bet_commit`    | Player committed a bet; payload has roundId, egmId, totalStake (or localStake). |
| `round_result`  | Round finished; payload has winningNumber, sector, winAmount, netWin, balanceAfterWin, layout, RNG proof, etc. **Only this event triggers GAME_UPDATE to EGM.** |
| `round_state`   | Round state (e.g. betting open/closed, countdown). Stored only. |
| `round_summary` | Round summary stats (totalWagered, totalPaidOut, duration, etc.). Stored only. |
| `round_void`    | Round voided. Stored only. |
| `refund`        | Refund for a round. Stored only. |
| `cashout`       | Cashout requested from game side. Stored only. |
| `AFT_CONFIRMED` | AFT confirmation. Stored only. |
| `error`         | Error (code, message, severity, action, details). Stored only. |
| `ui_pong`       | Response to ui_ping. Stored only. |

All Roulette events are **stored** for audit/log; only **round_result** drives the adapter to send **GAME_UPDATE** to the EGM.

### 3.4 Server → EGM (outbound)

| Event         | When sent | Purpose |
|---------------|-----------|--------|
| `GAME_UPDATE` | After Roulette sends `round_result` and server has an active round (`_isRoundActive`) | Tells EGM the round result: `EventType`, `BetAmount`, `WinAmount`, `Timestamp`, `RoundId`, `EgmId`. Amounts are in “credits” (stake/win from Roulette divided by 100). |

---

## 4. User Journey: Money In, Bets, Cash Out

### 4.1 Put money in

**Option A — Cash (bill validator)**  
1. User inserts cash in the EGM.  
2. EGM sends **BILL_INSERTED** to the server (e.g. `amount`, `CurrentCredits`, `egmId`).  
3. Server translates to **cash_event** and sends to Roulette (denomination in cents, currency ZAR, meters).  
4. Server sends **balance_snapshot** to Roulette with `payload.availableCredits`.  
5. Roulette can show updated balance and allow betting.

**Option B — AFT deposit**  
1. User does an account transfer deposit on the EGM.  
2. EGM sends **AFT_DEPOSIT** (e.g. `Amount`, `CurrentCredits`, `AFTReference`, `egmId`).  
3. Server translates to **aft_transfer** and sends to Roulette.  
4. Server sends **balance_snapshot** with current credits.  
5. Roulette updates balance and allows betting.

**Startup / first credits**  
- When the EGM sends **session_initialized** (once at startup, with `client: "EGM_Application"` and `payload.availableCredits`), the server forwards it to Roulette. This is the only way the server sends session_initialized to Roulette for EGM startup; CONNECTION_TEST is not used for that.  
- So “credits to the game” at startup and after each cash/credit event are delivered via **session_initialized** (once) and **balance_snapshot** (after each credit change).

---

### 4.2 Place a bet and play a round

1. Roulette has received session + balance (session_initialized and/or balance_snapshot) and shows credits.  
2. User places a bet in the Roulette UI.  
3. Roulette sends **bet_commit** to the server (roundId, egmId, totalStake or localStake).  
4. Server stores the message, sets `_pendingBetStake` and `_isRoundActive`, and sends **bet_commit_ack** back to Roulette (accepted, egmBalanceBefore, egmBalanceAfter, meters).  
5. Roulette runs the round (e.g. spin, ball, result).  
6. EGM may send **SPIN_COMPLETED** when its spin animation finishes; server forwards it to Roulette as **SPIN_COMPLETED**.  
7. When the round ends, Roulette sends **round_result** to the server (winningNumber, sector, winAmount, netWin, balanceAfterWin, layout, RNG proof, etc.).  
8. If the server has an active round (`_isRoundActive`), it:  
   - Builds **GAME_UPDATE** with `BetAmount` = _pendingBetStake/100, `WinAmount` = round_result winAmount/100.  
   - Sends **GAME_UPDATE** to the EGM only.  
   - Updates internal balance and meters, then clears _pendingBetStake and _isRoundActive.  
9. Roulette may also send **round_summary**, **round_state**, **refund**, **round_void**, **error**; these are stored only and do not trigger messages to the EGM.

So: **user puts money in** (EGM → BILL_INSERTED/AFT_DEPOSIT → server → cash_event/aft_transfer + balance_snapshot → Roulette); **user takes bets** (Roulette → bet_commit → server → bet_commit_ack → Roulette, then round_result → server → GAME_UPDATE → EGM).

---

### 4.3 Cash out

1. User requests cash out on the EGM.  
2. EGM sends **AFT_CASHOUT** to the server (e.g. `Amount`, `CurrentCredits` = 0, optional `BetAmount`/`WinAmount`, `egmId`).  
3. Server translates to **cashout_ack** and sends to Roulette (roundId, accepted, egmBalanceBefore, egmBalanceAfter = 0, bet, win).  
4. Server sends **balance_snapshot** with `availableCredits: 0` to Roulette.  
5. Roulette can show zero balance and that the session is cashed out.

---

## 5. Server State (relevant to flow)

- **Client type per socket:** EGM or ROULETTE (from first message).  
- **Session:** `_sessionInitialized` — session_initialized is sent to Roulette once when EGM sends session_initialized (with `client: "EGM_Application"`).  
- **Round:** `_pendingBetStake`, `_isRoundActive` — set on bet_commit, cleared when round_result is processed and GAME_UPDATE is sent.  
- **Balance/meters:** `_currentEgmBalance`, `_coinsIn`, `_gamesPlayed` — updated on BILL_INSERTED, AFT_DEPOSIT, AFT_CASHOUT, and on round_result (balance = balance - stake + win).  

Credits are sent to the Roulette game via **session_initialized** (at EGM startup) and **balance_snapshot** (after every credit-changing EGM event). The only event sent from the server to the EGM is **GAME_UPDATE**, after a Roulette **round_result** for an active round.
