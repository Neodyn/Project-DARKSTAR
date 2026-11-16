-- D.A.R.K.S.T.A.R. chat bridge - server hook.
--
-- Copy to <Saved Games folder of the DCS SERVER>\Scripts\Hooks\ and restart the server. Nothing
-- has to be installed on any player's PC. A copy on a client does nothing: everything below only
-- runs where DCS.isServer() is true.
--
-- A player addresses the bot in the chat, starting with a marker and one of the bot's callsigns:
--
--     @Overlord picture
--     >> Overlord: bogey dope
--     » Overlord: Picture          (what the radio wheel sends)
--
-- The hook hides such a message from everybody else and files it for Darkstar, which answers over
-- the radio exactly as it answers an F10 menu click. The sender still sees their own line: DCS
-- shows a player's own chat locally before the server is asked, and no hook can take it back
-- (measured in the chat test of November 2026). That is why the line is meant to be readable.
--
-- Which callsigns count is decided by Darkstar, not here: while the bot runs it rewrites
-- <Saved Games>\Darkstar\ChatBridge\callsigns.txt every few seconds. A missing or stale file means
-- the bot is not running, and then this hook lets every message through untouched - swallowing a
-- request nobody is going to answer would be worse than showing it.
--
-- Request files: <Saved Games>\Darkstar\ChatBridge\In\*.txt, written as .tmp and renamed, so
-- Darkstar never reads half a file. Format: first line "darkstar-chat 1", then key=value lines.

local lfs = require('lfs')

local root = lfs.writedir() .. 'Darkstar'                    -- '\\' is required: Lua 5.1 turns '\D' into 'D'
local base = root .. '\\ChatBridge'
local inDir = base .. '\\In'
local callsignFile = base .. '\\callsigns.txt'

local STALE_AFTER = 60      -- seconds without a heartbeat before the bot counts as not running
local RECHECK_EVERY = 5     -- seconds between two looks at callsigns.txt

-- '\194\187' is "»" in UTF-8, spelled as bytes so the file's own encoding cannot break it.
local MARKERS = { '\194\187', '>>', '@' }

local callsigns = {}
local checkedAt = -RECHECK_EVERY
local filed = 0

local function note(text)
  pcall(function() log.write('DARKSTAR-CHAT', log.INFO, text) end)
end

local function trim(s)
  return (s:gsub('^%s+', ''):gsub('%s+$', ''))
end

local function oneLine(value)
  return (tostring(value or ''):gsub('[\r\n]', ' '))
end

-- Reads callsigns.txt again when it is due, and forgets every callsign once the heartbeat stops.
local function refresh()
  local now = os.time()
  if now - checkedAt < RECHECK_EVERY then return end
  checkedAt = now

  local modified = lfs.attributes(callsignFile, 'modification')
  if not modified or now - modified > STALE_AFTER then
    callsigns = {}
    return
  end

  local h = io.open(callsignFile, 'r')
  if not h then
    callsigns = {}
    return
  end

  local list = {}
  for line in h:lines() do
    line = trim(line)
    if line ~= '' and line:sub(1, 1) ~= '#' then list[#list + 1] = line end
  end
  h:close()

  -- Longest first, so "Overlord Two" is tried before "Overlord".
  table.sort(list, function(a, b) return #a > #b end)
  callsigns = list
end

-- "@Overlord picture" -> "Overlord", "picture". Nil when the message is not for the bot.
local function addressed(msg)
  local rest
  for _, marker in ipairs(MARKERS) do
    if msg:sub(1, #marker) == marker then
      rest = msg:sub(#marker + 1)
      break
    end
  end
  if not rest then return nil end

  rest = (rest:gsub('^%s+', ''))
  local lower = rest:lower()

  for _, callsign in ipairs(callsigns) do
    local wanted = callsign:lower()
    if lower:sub(1, #wanted) == wanted then
      local after = rest:sub(#wanted + 1)
      -- The callsign has to end where it ends: "@Overlordish" is not addressed to "Overlord".
      if after == '' or after:match('^[%s,:;%.!%-]') then
        return callsign, trim((after:gsub('^[%s,:;%.!%-]+', '')))
      end
    end
  end

  return nil
end

-- The player's group, which is what the bot locates them by. Empty for a spectator.
local function groupOf(slot)
  local unit = tonumber(tostring(slot or ''):match('^(%d+)'))
  if not unit then return '', '' end

  local name, id = '', ''
  pcall(function()
    name = DCS.getUnitProperty(unit, DCS.UNIT_GROUPNAME) or ''
    id = DCS.getUnitProperty(unit, DCS.UNIT_GROUP_MISSION_ID) or ''
  end)
  return tostring(name), tostring(id)
end

-- Writes one request. False when it could not, so the caller lets the message through instead.
local function file(id, callsign, text, to)
  local info = net.get_player_info(id) or {}
  local group, groupId = groupOf(info.slot)

  lfs.mkdir(root)
  lfs.mkdir(base)
  lfs.mkdir(inDir)

  filed = filed + 1
  local path = string.format('%s\\%010d-%05d', inDir, os.time(), filed % 100000)
  local h = io.open(path .. '.tmp', 'w')
  if not h then return false end

  h:write('darkstar-chat 1\n')
  h:write('player=', oneLine(info.name), '\n')
  h:write('ucid=', oneLine(info.ucid), '\n')
  h:write('side=', oneLine(info.side), '\n')
  h:write('group=', oneLine(group), '\n')
  h:write('groupid=', oneLine(groupId), '\n')
  h:write('callsign=', oneLine(callsign), '\n')
  h:write('to=', oneLine(to), '\n')
  h:write('text=', oneLine(text), '\n')
  h:close()

  return os.rename(path .. '.tmp', path .. '.txt') and true or false
end

local cb = {}

function cb.onPlayerTrySendChat(id, msg, to)
  if type(msg) ~= 'string' then return end

  local okServer, isServer = pcall(DCS.isServer)
  if not okServer or not isServer then return end

  refresh()
  if #callsigns == 0 then return end

  local callsign, text = addressed(trim(msg))
  if not callsign then return end

  local ok, written = pcall(file, id, callsign, text, to)
  if not ok or not written then
    note('could not file a request, letting it through: ' .. tostring(written))
    return
  end

  return ''   -- hidden from everybody else; the sender keeps their own line
end

DCS.setUserCallbacks(cb)
note('loaded, request folder ' .. inDir)
