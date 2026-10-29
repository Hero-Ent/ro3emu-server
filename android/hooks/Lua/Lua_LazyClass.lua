-- Framework/Core/Lua_LazyClass for the Android port: the client's LazyClass, rebuilt from its bytecode
-- (Recovery/LuaPayload/Framework/Core/Lua_LazyClass.lua.bytes, read with luac -l -l), with one addition marked below.
-- Ro3Android.Hooks.Lua hands this file to the game instead of the client's chunk.
--
-- The addition: the mobile UI's views lack widgets that the controllers they share with the PC UI use without a check
-- (UI_Sys_BagBaseCtrl wires View.m_kBTN_Auto, which only UI_Sys_BagWindowView, the PC bag, has). On the PC client the
-- mobile path never runs; here the nil aborts the screen's setup halfway (the bag opened with no items). A missing
-- m_k* field of a *View class now reads as a stand-in that ignores every call and returns nil (port behaviour, not
-- the client's), and the first read of each one is logged. The framework's own component binding keeps skipping it as
-- it skips a nil component (see skipMissingInRefresh).

local function construct(obj, cls, ...)
  if cls.super then
    construct(obj, cls.super, ...)
  end
  if cls.ctor then
    cls.ctor(obj, ...)
  end
end

local function delete(obj)
  if obj == nil then
    return nil
  end
  local cls = obj.__vtbl
  while cls ~= nil do
    if cls.dtor then
      cls.dtor(obj)
    end
    cls = cls.super
  end
end

-- Port addition: the stand-in for a widget a view does not have.
local missing = setmetatable({}, {
  __index = function(self) return self end,
  __newindex = function() end,
  __call = function() return nil end,
  __tostring = function() return "Ro3Android missing widget" end,
})
local reported = {}

-- The UI framework's binding (Lua_UIBase) does check its components: View[name] == nil is logged and skipped. The
-- stand-in passes that check, so Lua_UIComponentFormatNew.RefreshComponent (handlers[component:GetComponentType()],
-- no nil check) must skip it the way the framework skips a nil one. Wrapped when require registers the module.
local function skipMissingInRefresh(class)
  local refresh = class.RefreshComponent
  if type(refresh) ~= "function" or rawget(class, "__ro3SkipsMissing") then
    return
  end
  rawset(class, "__ro3SkipsMissing", true)
  class.RefreshComponent = function(self, component, ...)
    if rawequal(component, missing) then
      return
    end
    return refresh(self, component, ...)
  end
end

-- Port adjustment (inferred, not the client's data): the mobile bag's character preview. Its export entry in
-- Lua_ModelTransformDefine (UI_Sys_BagPanel: scale 1.8, offsetPosition 1.2699, -1.21167) frames the model about 5.2
-- tall in a view 4.55 tall (ModelDisplayCamera: FOV 16.5 at 15.7 from the model, measured on the phone), so the head and
-- the boots fell outside the window. Like the mobile views, that entry looks stale in the PC client. The offset is added
-- to the window's anchor unscaled (LC_UIModelManager, non-default entries), so: scale 1.1 (about 2.9 tall), the
-- client's x offset (centred between the equipment slots and the item list) and y -0.85 (feet above the preset bar,
-- head under the title bar), measured on a 2340x1080 phone.
local function fitBagPreview(transforms)
  local entry = rawget(transforms, "UI_Sys_BagPanel")
  if type(entry) ~= "table" or rawget(entry, "__ro3Fitted") then
    return
  end
  rawset(entry, "__ro3Fitted", true)
  local offset = entry.offsetPosition or {}
  entry.scale = { x = 1.1, y = 1.1, z = 1.1 }
  entry.offsetPosition = { x = offset.x or 0, y = -0.85, z = offset.z or 0 }
  pcall(function() CS.UnityEngine.Debug.Log("[Ro3Android] bag preview framing: scale 1.1") end)
end

do
  local loaded = package.loaded
  local meta = getmetatable(loaded) or {}
  local previous = meta.__newindex
  meta.__newindex = function(t, k, v)
    if type(k) == "string" and type(v) == "table" and k:lower():find("lua_uicomponentformatnew", 1, true) then
      skipMissingInRefresh(v)
    end
    if type(k) == "string" and type(v) == "table" and k:lower():find("lua_modeltransformdefine", 1, true) then
      fitBagPreview(v)
    end
    if previous then
      previous(t, k, v)
    else
      rawset(t, k, v)
    end
  end
  setmetatable(loaded, meta)
end

-- Port adjustment (inferred, not the client's data): the mobile bag's category tabs. UI_Sys_BagBaseCtrl (shared with the
-- PC bag) builds its tabs from View.m_kCLICKEVENT_Type<n> (SetActive, SetOnClick) and View.m_kTTXT_Select<n>, the PC
-- bag's names (UI_Sys_BagWindowView). UI_Sys_BagPanelView has toggles there (m_kTOGGLE_Type1..5, and m_kTTXT_Select
-- for tab 2), so every tab got the stand-in: no click handler, and the bag stayed on its first category. A tab now
-- reads as its toggle, with SetOnClick(cb, ...) mapped to Lua_UIToggleNew:SetOnChangedIsOn(cb, ...), which calls
-- cb(...) when the toggle turns on.
local function toggleAsClickEvent(toggle)
  return setmetatable({
    SetOnClick = function(_, cb, ...)
      return toggle:SetOnChangedIsOn(cb, ...)
    end,
  }, {
    __index = function(_, k)
      local v = toggle[k]
      if type(v) == "function" then
        return function(_, ...) return v(toggle, ...) end
      end
      return v
    end,
  })
end

local viewAliases = {
  UI_Sys_BagPanelView = function(view, key)
    local n = key:match("^m_kCLICKEVENT_Type(%d)$")
    if n then
      local toggle = view["m_kTOGGLE_Type" .. n]
      if toggle ~= nil and not rawequal(toggle, missing) then
        return toggleAsClickEvent(toggle)
      end
    elseif key == "m_kTTXT_Select2" then
      return view.m_kTTXT_Select
    end
  end,
}

local function missingWidget(cls, key, view)
  local name = cls.__classname
  if type(name) ~= "string" or type(key) ~= "string" or name:sub(-4) ~= "View" or key:sub(1, 3) ~= "m_k" then
    return nil
  end
  local alias = viewAliases[name] and viewAliases[name](view, key)
  if alias ~= nil then
    rawset(view, key, alias)
    return alias
  end
  local id = name .. "." .. key
  if not reported[id] then
    reported[id] = true
    pcall(function() CS.UnityEngine.Debug.LogWarning("[Ro3Android] view " .. id .. " is missing: stand-in") end)
  end
  return missing
end

function LazyClass(name, super)
  local cls = {}
  cls.__classname = name
  cls.__class = cls
  cls.Get = {}
  setmetatable(cls, {
    __call = function(...)
      return cls.new(...)
    end,
  })
  local getters = cls.Get
  if super then
    for k, v in pairs(super.Get) do
      getters[k] = v
    end
    cls.super = super
  end
  cls.__index = function(t, k)
    local v = cls[k]
    if v then
      return v
    end
    local getter = getters[k]
    if getter then
      return getter(t)
    end
    if cls.super then
      local inherited = cls.super[k]
      -- The client returns super[k] as it is; the port only steps in when that is nil too.
      if inherited ~= nil then
        return inherited
      end
    end
    return missingWidget(cls, k, t)
  end
  cls.__newindex = function(t, k, v)
    if getters[k] then
      assert(false, "can not set value to readonly property")
    end
    rawset(t, k, v)
  end
  cls.New = function(...)
    local obj = setmetatable({}, cls)
    obj.__vtbl = cls
    construct(obj, cls, ...)
    obj.Delete = delete
    return obj
  end
  return cls
end
