obs = obslua
local ffi = require("ffi")

ffi.cdef[[
typedef void *HANDLE;
typedef int BOOL;
typedef unsigned long DWORD;
HANDLE CreateFileA(const char *name, DWORD access, DWORD share, void *security, DWORD creation, DWORD flags, HANDLE template_file);
BOOL WaitNamedPipeA(const char *name, DWORD timeout_ms);
BOOL WriteFile(HANDLE file, const void *buffer, DWORD bytes, DWORD *written, void *overlapped);
BOOL ReadFile(HANDLE file, void *buffer, DWORD bytes, DWORD *read, void *overlapped);
BOOL CloseHandle(HANDLE object);
DWORD GetCurrentProcessId(void);
]]

local kernel32 = ffi.load("kernel32")
local PIPE_NAME = "\\\\.\\pipe\\inputd-obs-bridge-v1"
local GENERIC_READ = 0x80000000
local GENERIC_WRITE = 0x40000000
local OPEN_EXISTING = 3
local INVALID_HANDLE_VALUE = ffi.cast("HANDLE", -1)
local enabled = true
local bridge_path = ""
local paired_active = false
local recording_counter = 0

local function log(level, message)
    obs.script_log(level, "[inputd OBS] " .. message)
end

local function json_escape(value)
    value = value or ""
    return value:gsub('[%z\1-\31\\"]', function(character)
        if character == '"' then return '\\"' end
        if character == '\\' then return '\\\\' end
        if character == '\b' then return '\\b' end
        if character == '\f' then return '\\f' end
        if character == '\n' then return '\\n' end
        if character == '\r' then return '\\r' end
        if character == '\t' then return '\\t' end
        return string.format('\\u%04x', string.byte(character))
    end)
end

local function quote_command_argument(value)
    return '"' .. (value or ""):gsub('"', '\\"') .. '"'
end

local function launch_bridge()
    if bridge_path == nil or bridge_path == "" then
        log(obs.LOG_ERROR, "未配置 inputd-obs-bridge.exe。")
        return false
    end
    local pid = tonumber(kernel32.GetCurrentProcessId())
    local command = 'cmd.exe /d /c start "" /b ' .. quote_command_argument(bridge_path) ..
        ' ensure --parent-pid ' .. tostring(pid)
    os.execute(command)
    return true
end

local function pipe_request(action, video_path, retry)
    if not kernel32.WaitNamedPipeA(PIPE_NAME, retry and 5000 or 250) then
        if not retry and launch_bridge() then return pipe_request(action, video_path, true) end
        return nil, "录制助手命名管道不可用"
    end

    local handle = kernel32.CreateFileA(PIPE_NAME, GENERIC_READ + GENERIC_WRITE, 0, nil, OPEN_EXISTING, 0, nil)
    if handle == INVALID_HANDLE_VALUE then return nil, "无法连接录制助手" end
    local pid = tonumber(kernel32.GetCurrentProcessId())
    local request = string.format('{"version":1,"action":"%s","video_path":"%s","obs_process_id":%d}\n',
        json_escape(action), json_escape(video_path or ""), pid)
    local written = ffi.new("DWORD[1]")
    local write_ok = kernel32.WriteFile(handle, request, #request, written, nil)
    if write_ok == 0 or tonumber(written[0]) ~= #request then
        kernel32.CloseHandle(handle)
        return nil, "向录制助手发送事件失败"
    end

    local buffer = ffi.new("char[?]", 32768)
    local read = ffi.new("DWORD[1]")
    local read_ok = kernel32.ReadFile(handle, buffer, 32767, read, nil)
    kernel32.CloseHandle(handle)
    if read_ok == 0 then return nil, "未收到录制助手响应" end
    return ffi.string(buffer, tonumber(read[0]))
end

local function current_recording_path()
    local path = obs.obs_frontend_get_current_record_output_path()
    return path or ""
end

local function last_recording_path()
    local path = obs.obs_frontend_get_last_recording()
    if path == nil or path == "" then path = current_recording_path() end
    return path or ""
end

local function provisional_recording_path()
    recording_counter = recording_counter + 1
    local root = os.getenv("LOCALAPPDATA") or script_path()
    local pid = tonumber(kernel32.GetCurrentProcessId())
    return root .. "\\inputd\\obs-spool\\obs-" .. tostring(pid) .. "-" .. tostring(os.time()) ..
        "-" .. tostring(recording_counter) .. ".video"
end

local function send_event(action, path)
    local response, err = pipe_request(action, path, false)
    if response == nil then
        log(obs.LOG_ERROR, action .. " 失败：" .. tostring(err))
        return false
    end
    if not response:find('"ok":true', 1, true) then
        log(obs.LOG_ERROR, action .. " 被录制助手拒绝：" .. response)
        return false
    end
    log(obs.LOG_INFO, action .. " 完成：" .. response)
    return true
end

local function on_frontend_event(event)
    if not enabled then return end
    if event == obs.OBS_FRONTEND_EVENT_RECORDING_STARTED then
        -- OBS 30 can return only the configured directory at this event. Start
        -- immediately in a private spool, then move atomically to the exact
        -- obs_frontend_get_last_recording() stem after RECORDING_STOPPED.
        paired_active = send_event("start", provisional_recording_path())
    elseif event == obs.OBS_FRONTEND_EVENT_RECORDING_STOPPED then
        if paired_active then send_event("stop_async", last_recording_path()) end
        paired_active = false
    elseif event == obs.OBS_FRONTEND_EVENT_SCRIPTING_SHUTDOWN or event == obs.OBS_FRONTEND_EVENT_EXIT then
        if paired_active then send_event("stop_async", last_recording_path()) end
        paired_active = false
    end
end

function script_description()
    return [[<b>inputd OBS 配套操作录制</b><br>
OBS 开始录制时自动启动无损 Raw Input 记录，停止后在视频目录生成同名
<code>视频基名.input.jsonl</code> 和完整性清单。]]
end

function script_defaults(settings)
    obs.obs_data_set_default_bool(settings, "enabled", true)
    obs.obs_data_set_default_string(settings, "bridge_path", script_path() .. "bridge\\inputd-obs-bridge.exe")
end

function script_properties()
    local properties = obs.obs_properties_create()
    obs.obs_properties_add_bool(properties, "enabled", "跟随 OBS 自动录制操作")
    obs.obs_properties_add_path(properties, "bridge_path", "录制助手路径", obs.OBS_PATH_FILE,
        "Windows executable (*.exe)", script_path())
    return properties
end

function script_update(settings)
    enabled = obs.obs_data_get_bool(settings, "enabled")
    bridge_path = obs.obs_data_get_string(settings, "bridge_path")
    if enabled then launch_bridge() end
end

function script_load(settings)
    script_update(settings)
    obs.obs_frontend_add_event_callback(on_frontend_event)
    log(obs.LOG_INFO, "插件已加载；等待 OBS 录制事件。")
end

function script_unload()
    obs.obs_frontend_remove_event_callback(on_frontend_event)
    if paired_active then send_event("stop_async", last_recording_path()) end
    paired_active = false
end
