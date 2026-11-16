-- D.A.R.K.S.T.A.R. radio wheel - client hook.
--
-- Goes on the PLAYER's PC: copy to <Saved Games>\DCS\Scripts\Hooks\ (the folder of the DCS you fly
-- with) and restart DCS. It belongs with Darkstar.Wheel.exe, which writes one line per wheel pick
-- into %TEMP%\Darkstar-Wheel\Out\. This hook sends each line into the DCS chat, where the server
-- hook (Darkstar-ChatBridge.lua) hides it from everybody else and hands it to Darkstar.
--
-- Only while connected to a server as a client: in single player there is nobody to send it to, and
-- a player hosting the mission is the server - there the line would go to nobody either. Lines are
-- then dropped, not kept: a request picked an hour ago in the main menu must not go out when the
-- next mission starts.

local lfs = require('lfs')

-- TMP before TEMP: that is the order Windows (and Path.GetTempPath in the wheel) uses.
local tmp = os.getenv('TMP') or os.getenv('TEMP') or ''
local outDir = tmp .. '\\Darkstar-Wheel\\Out'      -- '\\' is required: Lua 5.1 turns '\D' into 'D'

local MAX_AGE = 10       -- seconds; an older pick is dropped instead of sent
local POLL_EVERY = 0.2   -- seconds between two looks at the folder

local nextPoll = 0

local function note(text)
  pcall(function() log.write('DARKSTAR-WHEEL', log.INFO, text) end)
end

local function connectedAsClient()
  local okM, multiplayer = pcall(DCS.isMultiplayer)
  local okS, server = pcall(DCS.isServer)
  return okM and okS and multiplayer == true and server == false
end

local function readLine(path)
  local h = io.open(path, 'r')
  if not h then return nil end
  local line = h:read('*l')
  h:close()
  return line
end

local cb = {}

function cb.onSimulationFrame()
  local now = os.clock()
  if now < nextPoll then return end
  nextPoll = now + POLL_EVERY

  if not lfs.attributes(outDir) then return end

  local files = {}
  for f in lfs.dir(outDir) do
    if f:sub(-4) == '.txt' then files[#files + 1] = f end
  end
  if #files == 0 then return end
  table.sort(files)   -- the wheel names its files by time, so this is the order they were picked in

  local client = connectedAsClient()
  local wallClock = os.time()

  for _, f in ipairs(files) do
    local path = outDir .. '\\' .. f
    local modified = lfs.attributes(path, 'modification') or 0
    local line = readLine(path)
    os.remove(path)

    if not client then
      note('not connected to a server as a client - dropped ' .. f)
    elseif wallClock - modified > MAX_AGE then
      note('older than ' .. MAX_AGE .. ' s - dropped ' .. f)
    elseif line and #line > 0 then
      local ok, err = pcall(net.send_chat, line, false)
      if not ok then note('could not send ' .. f .. ': ' .. tostring(err)) end
    end
  end
end

DCS.setUserCallbacks(cb)
note('loaded, watching ' .. outDir)
