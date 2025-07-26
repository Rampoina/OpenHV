lost = false
towers = 0
Tick = function()
	if Human.Cash > 15000 and not lost then
		Media.DisplayMessage("Gasta els diners carallot!")
		Human.MarkFailedObjective(KeepMoneyLow)
		lost = true
	end
    if DateTime.GameTime % DateTime.Seconds(60) == 0 then
		local t = Human.GetActorsByType("miner2")

		if #t > towers then
		Media.DisplayMessage("Nombre de torres: " .. #t)
			towers = #t
		else
			Human.MarkFailedObjective(BuildTowers)
		end
	end

	if Human.IsProducing("module") then
		Media.DisplayMessage("Construint!")
	else
		Media.DisplayMessage("Construeix!")
	end

end

WorldLoaded = function()
    Human = Player.GetPlayer("Multi0")

	InitObjectives(Human)

	KeepMoneyLow = AddPrimaryObjective(Human, "money-low")
	BuildTowers = AddPrimaryObjective(Human, "build-towers")
end
