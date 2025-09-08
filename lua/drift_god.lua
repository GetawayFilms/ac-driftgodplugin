-- ===================================================================
-- DriftGodPlugin by Living God
-- Full-screen arcade drift scoring UI with editable configuration
-- ===================================================================

-- ===========================================
-- Load in the relevant fonts from the server
-- ===========================================
local fonts_loaded = false
local font_loading = false
local fonts_folder_path = ""

local function load_fonts()
    if font_loading or fonts_loaded then return end
    font_loading = true
    
    web.loadRemoteAssets("http://godzkitchen.ddns.net:8082/driftgod/fonts/fonts.zip", function(err, folder)
        if err then
            ac.log("DriftGod: Font loading failed - " .. err)
            font_loading = false
        else
            ac.log("DriftGod: Fonts loaded to - " .. folder)
            fonts_loaded = true
            fonts_folder_path = folder
            font_loading = false
        end
    end)
end

local function get_font_main()
    return fonts_loaded and (fonts_folder_path .. "\\Robotica.ttf") or "Arial"
end

local function get_font_message()
    return fonts_loaded and (fonts_folder_path .. "\\Mogra-Regular.ttf") or "Impact"
end

local function get_font_stats()
    return fonts_loaded and (fonts_folder_path .. "\\BrunoAceSC-Regular.ttf") or "Segoe UI"
end

-- ===================================
--Format the numbers for cleaner look
-- ===================================
local function format_number(num)
    if not num or type(num) ~= "number" then
        return "0"
    end
    local formatted = tostring(math.floor(num))
    local len = string.len(formatted)
    
    -- Add commas from right to left
    for i = len - 3, 1, -3 do
        formatted = string.sub(formatted, 1, i) .. "," .. string.sub(formatted, i + 1)
    end
    
    return formatted
end

-- ==================================================
-- Set up the universal sizes and positions for text
-- ==================================================
local UI_CONFIG = {
    
    -- Font sizes (absolute pixel values - bigger = larger text)
    score_font_size = 80,         -- Main drift score 
    combo_font_size = 30,          -- Combo multiplier  
    angle_font_size = 50,         -- Angle display
    stats_font_size = 24,          -- Statistics board
    praise_font_size = 40,        -- Praise messages
    warning_font_size = 30,        -- Warning messages
	bonus_font_size = 20,          -- Bonus messages

    
    -- Positions (scaled automatically for your resolution)
    score_x = 30,                  -- Main score X position
    score_y = 30,                  -- Main score Y position
    angle_x_from_right = 220,      -- Angle distance from right edge
    angle_y = 30,                  -- Angle Y position
    stats_x = 30,                  -- Stats X position
    stats_y_from_bottom = 130,     -- Stats distance from bottom
    message_y_praise = 70,         -- Praise messages Y position
    message_x_warning = 20,        -- Warning messages X offset
	message_y_warning = 60,        -- Warning messages Y position
	message_x_bonus = 30,          -- Bonus messages X offset
	message_y_bonus = 80,          -- Bonus messages Y position
	
    -- Spacing
    combo_y_offset = 80,           -- Space between score and combo
    stats_line_spacing = 30        -- Space between stat lines
}

-- =============================================================================

local connected = false

-- =====================
-- Core drift variables
-- =====================
local CurrentDriftTime = 0
local CurrentDriftTimeout = 2
local CurrentDriftScore = 0
local CurrentDriftScoreTarget = 0
local CurrentDriftCombo = 1
local TotalScore = 0
local TotalScoreTarget = 0
local BestDrift = 0
local BestDriftTarget = 0
local BestLapScore = 0
local BestLapScoreTarget = 0
local SecondsTimer = 0
local UpdatesTimer = 0
local LongDriftTimer = 0
local NoDriftTimer = 0
local SplineReached = 0
local CurrentLapScoreCut = false
local CurrentLapScoreCutValue = 0
local CurrentLapScore = 0
local CurrentLapScoreTarget = 0
local SubmittedLapDriftScore = 0

local ExtraScore = false
local ExtraScoreMultiplier = 1
local InitialScoreMultiplier = 0
local NearestCarDistance = 1

local NoWarning = true
local ComboReached = 0


-- =====================
-- Achievement tracking (prevent spam)
-- =====================
local LastAchievementTriggered = ""
local LastAchievementLevel = 0

-- =====================
-- Drift session tracking variables  
-- =====================
local CurrentDriftMaxAngle = 0
local CurrentDriftTotalTime = 0
local DriftIsActive = false

-- ============================
-- Personal Best from server
-- ============================
local PersonalBest = 0
local PersonalBestTarget = 0

-- =======================================
-- Screen detection and scaling variables
-- =======================================
local screen_width = 1920
local screen_height = 1080
local scale_factor = 1.0
local overlay_initialized = false

-- =======================
-- UI Animation variables
-- =======================
local PraiseText = ""
local PraiseTimer = 0
local PraiseScale = 0
local PraiseAlpha = 0
local WarningText = ""
local WarningTimer = 0
local WarningScale = 0
local WarningAlpha = 0
local BonusText = ""
local BonusTimer = 0
local BonusScale = 0
local BonusAlpha = 0

-- =========================
-- Track and car references
-- =========================
local TrackHasSpline = ac.hasTrackSpline()
local Sim = ac.getSim()
local Car = ac.getCar(0)

-- ============================
-- Drift calculation variables
-- ============================
local angle
local dirt

-- ====================
-- Animation constants
-- ====================
local PRAISE_DURATION = 2.0
local WARNING_DURATION = 2.0
local BONUS_DURATION = 2.0

-- =======
-- Colors
-- =======
local colorWhite = rgbm(1, 1, 1, 1)
local colorGreen = rgbm(0, 1, 0.1, 1)
local colorGreenBland = rgbm(0.65, 1, 0.65, 1)
local colorYellow = rgbm(1, 1, 0, 1)
local colorYellowBland = rgbm(1, 1, 0.5, 1)
local colorRed = rgbm(1, 0, 0, 1)
local colorOrange = rgbm(1, 0.5, 0, 1)


-- OnlineEvent definitions for server communication
local playerConnectEvent = ac.OnlineEvent({
    ac.StructItem.key("DriftGod_playerConnect"),
    connected = ac.StructItem.byte()
})

local driftCompleteEvent = ac.OnlineEvent({
    ac.StructItem.key("DriftGod_driftComplete"),
    score = ac.StructItem.int32(),
    avgAngle = ac.StructItem.float(),
    avgCombo = ac.StructItem.float(),
	duration = ac.StructItem.float()
})

local personalBestEvent = ac.OnlineEvent({
    ac.StructItem.key("DriftGod_personalBest"),
    personalBest = ac.StructItem.int64()
}, function(sender, data)
    if data then
        PersonalBest = tonumber(data.personalBest) or 0
        PersonalBestTarget = tonumber(data.personalBest)
    end
end)

local achievementEvent = ac.OnlineEvent({
    ac.StructItem.key("DriftGod_achievement"),
    achievementType = ac.StructItem.int32()
})

-- ==========================
-- Screen detection function
-- ==========================
local function detect_screen_size()
    local sim = ac.getSim()
    if sim and sim.windowWidth and sim.windowHeight then
        screen_width = sim.windowWidth
        screen_height = sim.windowHeight
        ac.log(string.format("DriftGod: Screen detected %dx%d", screen_width, screen_height))
        return true
    end
    
    local window_width = ui.windowWidth()
    local window_height = ui.windowHeight()
    
    if window_width and window_height then
        if window_width > 2500 then
            screen_width = window_width
            screen_height = window_height
        else
            screen_width = math.max(window_width * 2, 1920)
            screen_height = math.max(window_height * 2, 1080)
        end
        ac.log(string.format("DriftGod: Window scaled %dx%d -> %dx%d", window_width, window_height, screen_width, screen_height))
        return true
    end
    
    screen_width = 2560
    screen_height = 1440
    ac.log("DriftGod: Using fallback 1440p resolution")
    return false
end

-- ===========================================
-- Calculate scale factor based on resolution
-- ===========================================
local function calculate_scale_factor()
    local base_width = 1920
    local base_height = 1080
    
    local width_scale = screen_width / base_width
    local height_scale = screen_height / base_height
    scale_factor = math.min(width_scale, height_scale)
    
    scale_factor = math.max(0.5, math.min(scale_factor, 4.0))
    
    ac.log(string.format("DriftGod: Scale factor %.2fx for %dx%d", scale_factor, screen_width, screen_height))
end

-- ===============================
-- Helper function to scale sizes
-- ===============================
local function scaled(size)
    return math.floor(size * scale_factor)
end

-- =============================
-- Get nearby car for reference
-- =============================
function getNearbyCarDistance()
    local PlayerCarPos = Car.position
    local lowestDist = 9999999
    for i = 1, 9999 do
        local otherCar = ac.getCar(i)
        if otherCar and i ~= 0 then
            local distance = math.distance(Car.position, otherCar.position)
            if distance < lowestDist and (not otherCar.isInPit) and (not otherCar.isInPitlane) and otherCar.isConnected then
                lowestDist = distance
            end
        elseif not otherCar then
            break
        end
    end
    return lowestDist
end

-- ================================
-- Praise, Bonus and Warning setup
-- ================================
function showPraise(text)
    PraiseText = text
    PraiseTimer = PRAISE_DURATION
    PraiseScale = 0
    PraiseAlpha = 0
end

function showBonus(text)
    BonusText = text
    BonusTimer = BONUS_DURATION
    BonusScale = 0
    BonusAlpha = 0
end

function showWarning(text)
    WarningText = text
    WarningTimer = WARNING_DURATION
    WarningScale = 0
    WarningAlpha = 0
end

-- =============================
-- Send drift completion data
-- =============================
function sendDriftCompleted(score, angle, duration, combo)
    ac.log("DriftGod: Sending drift - Duration: " .. tostring(duration))
    driftCompleteEvent({
        score = score,
        avgAngle = angle,
        avgCombo = combo,
        duration = duration
    })
end

-- =============================
-- Send achievement data
-- =============================
function sendAchievement(achievement_type, value)
	ac.log("DriftGod: sendAchievement: " .. achievement_type)

    local achievementCode = 0
    if achievement_type == "geometry_student" then
        achievementCode = 1
    elseif achievement_type == "drift_specialist" then
        achievementCode = 2
    elseif achievement_type == "lateral_master" then
        achievementCode = 3
    elseif achievement_type == "professor_slideways" then
        achievementCode = 4
    elseif achievement_type == "drift_god" then
        achievementCode = 5
    end
    
    achievementEvent({
        achievementType = achievementCode
    })
end

-- =============================
-- Session Start Script Update
-- =============================
function script.update(dt)
    Sim = ac.getSim()
    Car = ac.getCar(0)

	if not connected then
		playerConnectEvent({connected = 1})
		connected = true
	end
    
    if not Sim.isPaused then

        SecondsTimer = SecondsTimer + dt
        UpdatesTimer = UpdatesTimer + 1
        
-- ======================
-- Calculate drift angle
-- ======================
        angle = math.max(0, ((math.max(math.abs(Car.wheels[2].slipAngle), math.abs(Car.wheels[3].slipAngle)))))
        if (Car.localVelocity.z <= 0 and Car.speedKmh > 1) then
            angle = 180 - angle
        end
		
		if Car.speedKmh < 2 then
			angle = 0
		end
        
        dirt = math.min(Car.wheels[0].surfaceDirt, Car.wheels[1].surfaceDirt, Car.wheels[2].surfaceDirt, Car.wheels[3].surfaceDirt)
        
-- =================
-- Main drift logic
-- =================
        if angle > 10 and Car.speedKmh > 20 and dirt == 0 and Car.wheelsOutside < 4 and ((not TrackHasSpline) or Car.splinePosition >= SplineReached - 0.0001) then
            -- Player is actively drifting
            if not DriftIsActive then
                DriftIsActive = true
                CurrentDriftMaxAngle = 0
                CurrentDriftTotalTime = 0
            end
			
            
            -- Track peak angle and total time during drift
            CurrentDriftMaxAngle = math.max(CurrentDriftMaxAngle, angle)
            CurrentDriftTotalTime = CurrentDriftTotalTime + dt
            
            CurrentDriftTimeout = math.min(1, CurrentDriftTimeout + dt)
            CurrentDriftScore = CurrentDriftScore + (((((angle - 10) * 10 + (Car.speedKmh - 20) * 10) * 1) * dt * CurrentDriftCombo)) * ExtraScoreMultiplier * InitialScoreMultiplier * 0.2
            CurrentDriftCombo = math.min(5, CurrentDriftCombo + (((((angle - 10) + (Car.speedKmh - 20)) * 0.25) * dt) / 100) * ExtraScoreMultiplier * InitialScoreMultiplier * 0.5)
            LongDriftTimer = LongDriftTimer + dt
            NoDriftTimer = 0.5
            InitialScoreMultiplier = math.min(1, LongDriftTimer)
            
            if ComboReached < CurrentDriftCombo then
                ComboReached = CurrentDriftCombo
            end
        elseif CurrentDriftCombo > 1 then
            -- Still in drift combo but not actively drifting
            CurrentDriftTimeout = math.min(1, CurrentDriftTimeout + dt)
            CurrentDriftCombo = math.max(1, CurrentDriftCombo - 0.1 * (NoDriftTimer ^ 2) * dt)
            NoDriftTimer = NoDriftTimer + dt
            LongDriftTimer = 0
        elseif CurrentDriftCombo == 1 and CurrentDriftTimeout > 0 then
            -- Drift combo ending
            CurrentDriftTimeout = CurrentDriftTimeout - dt
            NoDriftTimer = NoDriftTimer + dt
            LongDriftTimer = 0
        elseif CurrentDriftTimeout <= 0 then
            -- Drift completely ended
            CurrentDriftTimeout = 0
            LongDriftTimer = 0
            NoDriftTimer = NoDriftTimer + dt
            
            -- DRIFT END PROCESSING
            if DriftIsActive and NoWarning then
                DriftIsActive = false
                
                if CurrentDriftScore > 0 then
                    TotalScoreTarget = TotalScoreTarget + math.floor(CurrentDriftScore)
                    if TrackHasSpline then
                        SubmittedLapDriftScore = SubmittedLapDriftScore + math.max(0, math.floor(CurrentDriftScore))
                    end
                    if math.floor(CurrentDriftScore) > BestDriftTarget then
                        BestDriftTarget = math.floor(CurrentDriftScore)
                    end
					
					if math.floor(CurrentDriftScore) > PersonalBestTarget then
						PersonalBestTarget = math.floor(CurrentDriftScore)
					end
                    
                    -- DATABASE: Send completed drift data with CAPTURED values
                    sendDriftCompleted(
                        math.floor(CurrentDriftScore),
                        math.floor(CurrentDriftMaxAngle),  -- Peak angle during drift
                        CurrentDriftTotalTime,             -- Total drift duration  
                        ComboReached
                    )
                    
                    ac.log(string.format("DriftGod: Drift ended - Max Angle: %.1f°, Duration: %.1fs", 
                        CurrentDriftMaxAngle, CurrentDriftTotalTime))
                end
            end
            
            CurrentDriftScore = 0
            CurrentDriftCombo = 1
            ComboReached = 0
            LastAchievementTriggered = ""  -- Reset achievement tracking for next drift
        end
	
-- ===================
-- Handle lap scoring
-- ===================
        CurrentLapScoreTarget = CurrentLapScoreCutValue + SubmittedLapDriftScore + math.floor(CurrentDriftScore)
        if (not CurrentLapScoreCut) and CurrentLapScoreTarget < 0 then
            repeat
                if CurrentLapScoreTarget > CurrentLapScoreCutValue * 0.99 then
                    CurrentLapScoreCutValue = CurrentLapScoreCutValue * 0.99
                else
                    CurrentLapScoreCutValue = CurrentLapScoreCutValue + 1
                end
                CurrentLapScoreTarget = CurrentLapScoreCutValue + SubmittedLapDriftScore + math.floor(CurrentDriftScore)
            until CurrentLapScoreTarget >= 0
        end
        
-- =========================
-- Smooth value transitions
-- =========================
        if TotalScore ~= TotalScoreTarget then
            TotalScore = TotalScore + math.floor((TotalScoreTarget - TotalScore) / 50)
            if math.floor((TotalScoreTarget - TotalScore) / 50) == 0 then
                TotalScore = TotalScore + math.floor(TotalScoreTarget - TotalScore)
            end
        end
        if BestDrift ~= BestDriftTarget then
            BestDrift = BestDrift + math.floor((BestDriftTarget - BestDrift) / 50)
            if math.floor((BestDriftTarget - BestDrift) / 50) == 0 then
                BestDrift = BestDrift + math.floor(BestDriftTarget - BestDrift)
            end
        end
        if CurrentLapScore ~= CurrentLapScoreTarget then
            CurrentLapScore = CurrentLapScore + math.floor((CurrentLapScoreTarget - CurrentLapScore) / 50)
            if math.floor((CurrentLapScoreTarget - CurrentLapScore) / 50) == 0 then
                CurrentLapScore = CurrentLapScore + math.floor(CurrentLapScoreTarget - CurrentLapScore)
            end
        end
        if BestLapScore ~= BestLapScoreTarget then
            BestLapScore = BestLapScore + math.floor((BestLapScoreTarget - BestLapScore) / 50)
            if math.floor((BestLapScoreTarget - BestLapScore) / 50) == 0 then
                BestLapScore = BestLapScore + math.floor(BestLapScoreTarget - BestLapScore)
            end
        end
		
		if PersonalBest ~= PersonalBestTarget then
			PersonalBest = PersonalBest + math.floor((PersonalBestTarget - PersonalBest) / 50)
			if math.floor((PersonalBestTarget - PersonalBest) / 50) == 0 then
				PersonalBest = PersonalBest + math.floor(PersonalBestTarget - PersonalBest)
			end
		end
        
-- ===================
-- Warning conditions
-- ===================
        NoWarning = true
        if Car.speedKmh <= 20 then
            NoWarning = false
            if WarningTimer <= 0 then
                showWarning("")
            end
		elseif dirt > 0 or Car.wheelsOutside == 4 then
            NoWarning = false
            if WarningTimer <= 0 then
                showWarning("OFFROAD!")
            end
        elseif TrackHasSpline and Car.splinePosition < SplineReached - 0.0001 then
            NoWarning = false
            if WarningTimer <= 0 then
                showWarning("DRIVING BACKWARDS!")
            end
        end
        
-- ===================
-- Track spline logic
-- ===================
        if TrackHasSpline then
            if Car.lapTimeMs < 3000 or Car.splinePosition < 0.001 then
                SplineReached = 0
            elseif Car.splinePosition > SplineReached then
                SplineReached = Car.splinePosition
            elseif Car.splinePosition < SplineReached - 0.0001 then
                NoWarning = false
            end
            
            if Car.lapTimeMs < 1000 and CurrentLapScoreCut == false then
                CurrentLapScoreCut = true
                if CurrentLapScore > BestLapScore then
                    BestLapScoreTarget = CurrentLapScore
                end
                CurrentLapScoreTarget = 0
                SubmittedLapDriftScore = 0
                CurrentLapScoreCutValue = -math.floor(CurrentDriftScore)
            elseif Car.lapTimeMs >= 1000 then
                CurrentLapScoreCut = false
                if math.floor(CurrentDriftScore) == 0 and CurrentLapScoreTarget < -1 then
                    CurrentLapScoreCutValue = 0
                    CurrentLapScoreTarget = 0
                end
            end
        end
        
-- ==================================
-- Calculate extra score multipliers
-- ==================================
        ExtraScore = false
        ExtraScoreMultiplier = 1
        if NoWarning then
            if angle > 120 then
                ExtraScoreMultiplier = ExtraScoreMultiplier * 0
                ExtraScore = true
                LongDriftTimer = 0
				showBonus("SPIN OUT!")
            end
            if Car.brake > 0.05 or Car.handbrake > 0.05 then
                ExtraScoreMultiplier = ExtraScoreMultiplier * 0.5
                ExtraScore = true
				showBonus("BRAKING!")
            end
            if NearestCarDistance < 7.5 then
                ExtraScoreMultiplier = ExtraScoreMultiplier * 2
                ExtraScore = true
				showBonus("TANDEM TIME!")
            end
            if angle > 90 and angle <= 120 then
                ExtraScoreMultiplier = ExtraScoreMultiplier * 1.5
                ExtraScore = true
				showBonus("REVERSE ENTRY!")
            end
            if LongDriftTimer > 3 then
                local LongDriftBonus = math.ceil((LongDriftTimer / 6) * 10 + 6.666) / 10
                ExtraScoreMultiplier = ExtraScoreMultiplier * LongDriftBonus
                ExtraScore = true
				showBonus("EPIC SLIDE! x " .. LongDriftBonus)
            end
            if UpdatesTimer % 30 == 15 then
                NearestCarDistance = getNearbyCarDistance()
            end
            ExtraScoreMultiplier = math.ceil(ExtraScoreMultiplier * 20) / 20
        end
        
-- ==========================
-- Handle penalty conditions
-- ==========================
        if NoWarning == false and CurrentDriftCombo > 1 then
            ComboReached = 0
            CurrentDriftScore = CurrentDriftScore - (CurrentDriftScore * dt)
            if CurrentDriftScore > 0 then
                CurrentDriftCombo = math.max(1, CurrentDriftCombo - dt)
            else
                CurrentDriftCombo = 1
            end
        elseif NoWarning == false and CurrentDriftCombo == 1 then
            ComboReached = 0
            CurrentDriftScore = CurrentDriftScore - (CurrentDriftScore * 2 * dt)
        end
        
-- =====================
-- Show praise messages
-- =====================
        if NoWarning and PraiseTimer <= 0 then
			local currentAchievement = ""
			if CurrentDriftScore > 256000 then
				currentAchievement = "drift_god"
			elseif ComboReached >= 5 or CurrentDriftScore > 64000 then
				currentAchievement = "professor_slideways"
			elseif ComboReached >= 4 or CurrentDriftScore > 16000 then
				currentAchievement = "lateral_master"
			elseif ComboReached >= 3 or CurrentDriftScore > 4000 then
				currentAchievement = "drift_specialist"
			elseif ComboReached >= 2 or CurrentDriftScore > 1000 then
				currentAchievement = "geometry_student"
			end
			
			-- Only show praise AND send achievement if it's different from the last one
			if currentAchievement ~= "" and currentAchievement ~= LastAchievementTriggered then
				if currentAchievement == "drift_god" then
					showPraise("DRIFT GOD!")
				elseif currentAchievement == "professor_slideways" then
					showPraise("PROFESSOR SLIDEWAYS!")
				elseif currentAchievement == "lateral_master" then
					showPraise("LATERAL MASTER!")
				elseif currentAchievement == "drift_specialist" then
					showPraise("DRIFT SPECIALIST!")
				elseif currentAchievement == "geometry_student" then
					showPraise("GEOMETRY STUDENT!")
				end
				
				sendAchievement(currentAchievement, CurrentDriftScore)
				LastAchievementTriggered = currentAchievement
			end
		end
        
-- ==================
-- Update animations
-- ==================
        -- Update the praise animation section:
		if PraiseTimer > 0 then
			PraiseTimer = PraiseTimer - dt
			local progress = 1 - (PraiseTimer / PRAISE_DURATION)
			
			if progress < 0.2 then
				-- Pop-in phase: quick scale up with bounce
				local bounceProgress = progress / 0.2
				PraiseScale = bounceProgress * bounceProgress * 1.4  -- Overshoot to 130%
				PraiseAlpha = bounceProgress
			elseif progress < 0.3 then
				-- Settle phase: bounce back down to normal size
				local settleProgress = (progress - 0.2) / 0.1
				PraiseScale = 1.3 - (settleProgress * 0.3)  -- Back down to 100%
				PraiseAlpha = 1
			elseif progress < 0.7 then
				-- Hold phase: stable at normal size
				PraiseScale = 1
				PraiseAlpha = 1
			else
				-- Fade out phase
				local fadeProgress = (progress - 0.7) / 0.3
				PraiseScale = 1 - fadeProgress * 0.2  -- Shrink slightly as it fades
				PraiseAlpha = 1 - fadeProgress
			end
		end
		
		if BonusTimer > 0 then
            BonusTimer = BonusTimer - dt
            local progress = 1 - (BonusTimer / BONUS_DURATION)
            if progress < 0.4 then
                BonusScale = progress / 0.4
                BonusAlpha = progress / 0.4
            elseif progress < 0.7 then
                BonusScale = 1
                BonusAlpha = 1
            else
                local fadeProgress = (progress - 0.7) / 0.4
                BonusScale = 1 - fadeProgress * 0.3
                BonusAlpha = 1 - fadeProgress
            end
        end
		
       
        if WarningTimer > 0 then
            WarningTimer = WarningTimer - dt
            local progress = 1 - (WarningTimer / WARNING_DURATION)
            if progress < 0.2 then
                WarningScale = progress / 0.2
                WarningAlpha = progress / 0.2
            elseif progress < 0.8 then
                WarningScale = 1
                WarningAlpha = 1
            else
                local fadeProgress = (progress - 0.8) / 0.2
                WarningScale = 1 - fadeProgress * 0.3
                WarningAlpha = 1 - fadeProgress
            end
        end
    end
end

-- ===========================
-- Draw UI initialize scaling
-- ===========================

function script.drawUI()

    if not overlay_initialized then
        detect_screen_size()
        calculate_scale_factor()
        overlay_initialized = true
        ac.log(string.format("DriftGod: Initialized %dx%d with %.2fx scale", screen_width, screen_height, scale_factor))
    end
	
    load_fonts()
    
    ui.invisibleButton("driftgod_fullscreen", vec2(screen_width, screen_height))
    
    ui.beginOutline()
    
	-- =============================
    -- Color logic for main display
	-- =============================
    local mainColor = colorWhite
    if Car.speedKmh <= 20 or dirt > 0 or Car.wheelsOutside == 4 then
        mainColor = colorOrange
    elseif ExtraScore and ExtraScoreMultiplier ~= 1 then
        if ExtraScoreMultiplier >= 2 then
            mainColor = colorGreen
        elseif ExtraScoreMultiplier > 1 then
            mainColor = colorGreenBland
        elseif ExtraScoreMultiplier == 0 then
            mainColor = colorYellow
        elseif ExtraScoreMultiplier < 1 then
            mainColor = colorYellowBland
        end
    end
    
	-- =================
    -- Main drift score
	-- =================
    ui.pushDWriteFont(get_font_main())
    ui.setCursor(vec2(scaled(UI_CONFIG.score_x), scaled(UI_CONFIG.score_y)))
    ui.dwriteText(format_number(CurrentDriftScore), scaled(UI_CONFIG.score_font_size), mainColor)
    
	-- =================
    -- Combo multiplier
	-- =================
    ui.setCursor(vec2(scaled(UI_CONFIG.score_x), scaled(UI_CONFIG.score_y + UI_CONFIG.combo_y_offset)))
    local comboText = string.format("x%.1f", math.ceil(CurrentDriftCombo * 10) / 10)
    if ExtraScore then
        comboText = comboText .. string.format(" x%.1f", ExtraScoreMultiplier)
    end
    ui.dwriteText(comboText, scaled(UI_CONFIG.combo_font_size), mainColor)
    ui.popDWriteFont()
    
	-- =================
    -- Angle display
	-- =================
    if angle then
        local angleColor = colorRed
        if angle >= 50 then
            angleColor = colorGreen
        elseif angle >= 35 then
            angleColor = colorGreenBland
        elseif angle >= 20 then
            angleColor = colorWhite
        elseif angle >= 15 then
            angleColor = colorYellowBland
        elseif angle >= 10 then
            angleColor = colorYellow
        end
        
        ui.pushDWriteFont(get_font_main())
        ui.setCursor(vec2(screen_width - scaled(UI_CONFIG.angle_x_from_right), scaled(UI_CONFIG.angle_y)))
        ui.dwriteText(string.format("📐%2d°", math.floor(angle)), scaled(UI_CONFIG.angle_font_size), angleColor)
        ui.popDWriteFont()
    end
    
	-- =================
    -- Statistics board
	-- =================
    local stats_y = screen_height - scaled(UI_CONFIG.stats_y_from_bottom)
    ui.pushDWriteFont(get_font_stats())
    ui.setCursor(vec2(scaled(UI_CONFIG.stats_x), stats_y))
    ui.dwriteText("DRIFT STATS", scaled(UI_CONFIG.stats_font_size), colorRed)
    
    ui.setCursor(vec2(scaled(UI_CONFIG.stats_x), stats_y + scaled(UI_CONFIG.stats_line_spacing)))
    ui.dwriteText("PB: " .. format_number(PersonalBest), scaled(UI_CONFIG.stats_font_size), colorWhite)
    
    ui.setCursor(vec2(scaled(UI_CONFIG.stats_x), stats_y + scaled(UI_CONFIG.stats_line_spacing * 2)))
    ui.dwriteText("THIS SESSION: " .. format_number(BestDrift), scaled(UI_CONFIG.stats_font_size), colorWhite)
    
    if TrackHasSpline then
        ui.setCursor(vec2(scaled(UI_CONFIG.stats_x), stats_y + scaled(UI_CONFIG.stats_line_spacing * 3)))
        ui.dwriteText("LAP: " .. format_number(CurrentLapScore), scaled(UI_CONFIG.stats_font_size), colorWhite)
    end
    ui.popDWriteFont()
    
	-- =================
    -- Animated messages
	-- =================
    if PraiseTimer > 0 then
		ui.pushDWriteFont(get_font_main())
		ui.setCursor(vec2(0, scaled(UI_CONFIG.message_y_praise)))
		
		local scaledFontSize = scaled(UI_CONFIG.praise_font_size) * PraiseScale
		
		ui.dwriteTextAligned(PraiseText, scaledFontSize, ui.Alignment.Center, 
			ui.Alignment.Start, 
			vec2(0, 0), 
			false, rgbm(0, 1, 0, PraiseAlpha))
		ui.popDWriteFont()
	end
	
	if BonusTimer > 0 then
        ui.pushDWriteFont(get_font_message())
        ui.setCursor(vec2(- scaled(UI_CONFIG.message_x_bonus), screen_height - scaled(UI_CONFIG.message_y_bonus)))
        ui.dwriteTextAligned(BonusText, scaled(UI_CONFIG.bonus_font_size), ui.Alignment.End, 
    ui.Alignment.Start, 
    vec2(0, 0), 
    false, rgbm(1, 0.6, 0.1, BonusAlpha))
        ui.popDWriteFont()
    end
    
    if WarningTimer > 0 then
        ui.pushDWriteFont(get_font_message())
        ui.setCursor(vec2(- scaled(UI_CONFIG.message_x_warning), screen_height - scaled(UI_CONFIG.message_y_warning)))
        ui.dwriteTextAligned(WarningText, scaled(UI_CONFIG.warning_font_size), ui.Alignment.End, 
    ui.Alignment.Start, 
    vec2(0, 0), 
    false, rgbm(1, 0, 0, WarningAlpha))
        ui.popDWriteFont()
    end
    
    ui.endOutline(rgbm(0, 0, 0, 1), scaled(3))
end
