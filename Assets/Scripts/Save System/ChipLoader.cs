﻿using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using System.Linq;

public static class ChipLoader
{

    public static SavedChip[] GetAllSavedChips(string[] chipPaths)
    {
        var savedChips = new SavedChip[chipPaths.Length];

        // Read saved chips from file
        for (var i = 0; i < chipPaths.Length; i++)
        {
            var chipSaveString = SaveSystem.ReadFile(chipPaths[i]);
            SaveCompatibility.FixSaveCompatibility(ref chipSaveString);
            savedChips[i] = JsonUtility.FromJson<SavedChip>(chipSaveString);
        }

        foreach (var chip in savedChips)
            chip.ValidateDefaultData();

        return savedChips;
    }

    public static Dictionary<string, SavedChip> GetAllSavedChipsDic(string[] chipPaths)
    {
        return GetAllSavedChips(chipPaths).ToDictionary(chip => chip.Data.name);
    }

    public static async void LoadAllChips(string[] chipPaths, Manager manager)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var chipsToLoadDic = GetAllSavedChipsDic(chipPaths);

        var progressBar = ProgressBar.New("Loading All Chips...", wholeNumbers: true);
        progressBar.Open(0, chipsToLoadDic.Count + manager.builtinChips.Length);
        progressBar.SetValue(0, "Start Loading...");

        // Maintain dictionary of loaded chips (initially just the built-in chips)
        var loadedChips = new Dictionary<string, Chip>();
        var i = 0;
        for (; i < manager.builtinChips.Length; i++)
        {
            var builtinChip = manager.builtinChips[i];
            progressBar.SetValue(i, $"Loading '{builtinChip.chipName}'...");
            loadedChips.Add(builtinChip.chipName, builtinChip);
            await Task.Yield();
        }

        foreach (var chip in chipsToLoadDic)
        {
            progressBar.SetValue(i, $"Loading '{chip.Value.Data.name}'...");
            if (!loadedChips.ContainsKey(chip.Key))
            {
                try
                {
                    ResolveDependecy(chip.Value);
                }
                catch (Exception e)
                {
                    DLSLogger.LogWarning($"Custom Chip '{chip.Value.Data.name}' could not be loaded!", e.ToString());
                }
            }

            await Task.Yield();
        }

        progressBar.SetValue(progressBar.progressBar.maxValue, "Done!");
        progressBar.Close();
        DLSLogger.Log($"Load time: {sw.ElapsedMilliseconds}ms");

        // the simulation will never create Cyclic path so simple ricorsive descending graph explore shuld be fine
        async void ResolveDependecy(SavedChip chip)
        {
            foreach (var dependancy in chip.ChipDependecies)
            {
                if (string.Equals(dependancy, "SIGNAL IN") || string.Equals(dependancy, "SIGNAL OUT")) continue;
                if (!loadedChips.ContainsKey(dependancy))
                { ResolveDependecy(chipsToLoadDic[dependancy]); await Task.Yield(); i++; }
            }
            if (!loadedChips.ContainsKey(chip.Data.name))
            {
                Chip loadedChip = manager.LoadChip(LoadChip(chip, loadedChips, manager.wirePrefab));
                loadedChips.Add(loadedChip.chipName, loadedChip);
            }
        }

    }

    // Instantiates all components that make up the given chip, and connects them
    // up with wires The components are parented under a single "holder" object,
    // which is returned from the function
    static ChipSaveData LoadChip(SavedChip chipToLoad, Dictionary<string, Chip> previouslyLoadedChips, Wire wirePrefab)
    {

        bool WouldLoad(out List<string> ComponentsMissing)
        {
            ComponentsMissing = new List<string>();
            foreach (var dependency in chipToLoad.ChipDependecies)
            {
                if (string.Equals(dependency, "SIGNAL IN") || string.Equals(dependency, "SIGNAL OUT")) continue;
                if (!previouslyLoadedChips.ContainsKey(dependency))
                    ComponentsMissing.Add(dependency);
            }
            return ComponentsMissing.Count <= 0;
        }


        if (!WouldLoad(out List<string> miss))
        {
            string MissingComp = "";
            for (int i = 0; i < miss.Count; i++)
            {
                MissingComp += miss[i];
                if (i < miss.Count - 1)
                    MissingComp += ",";
            }
            DLSLogger.LogError($"Failed to load {chipToLoad.Data.name} sub component: {MissingComp} was missing");

            return null;
        }

        ChipSaveData loadedChipData = new ChipSaveData();
        int numComponents = chipToLoad.savedComponentChips.Length;
        loadedChipData.componentChips = new Chip[numComponents];
        loadedChipData.Data = chipToLoad.Data;


        // Spawn component chips (the chips used to create this chip)
        // These will have been loaded already, and stored in the
        // previouslyLoadedChips dictionary
        for (int i = 0; i < numComponents; i++)
        {
            SavedComponentChip componentToLoad = chipToLoad.savedComponentChips[i];
            string componentName = componentToLoad.chipName;
            Vector2 pos = new Vector2((float)componentToLoad.posX, (float)componentToLoad.posY);


            Chip loadedComponentChip = GameObject.Instantiate(
                previouslyLoadedChips[componentName], pos, Quaternion.identity);
            loadedChipData.componentChips[i] = loadedComponentChip;

            // Load input pin names
            for (int inputIndex = 0;
                 inputIndex < componentToLoad.inputPins.Length &&
                 inputIndex < loadedChipData.componentChips[i].inputPins.Length;
                 inputIndex++)
            {
                loadedChipData.componentChips[i].inputPins[inputIndex].pinName =
                    componentToLoad.inputPins[inputIndex].name;
                loadedChipData.componentChips[i].inputPins[inputIndex].wireType =
                    componentToLoad.inputPins[inputIndex].wireType;
            }

            // Load output pin names
            for (int ouputIndex = 0; ouputIndex < componentToLoad.outputPins.Length;
                 ouputIndex++)
            {
                loadedChipData.componentChips[i].outputPins[ouputIndex].pinName =
                    componentToLoad.outputPins[ouputIndex].name;
                loadedChipData.componentChips[i].outputPins[ouputIndex].wireType =
                    componentToLoad.outputPins[ouputIndex].wireType;
            }
        }

        // Connect pins with wires
        for (int chipIndex = 0; chipIndex < chipToLoad.savedComponentChips.Length;
             chipIndex++)
        {
            Chip loadedComponentChip = loadedChipData.componentChips[chipIndex];
            for (int inputPinIndex = 0;
                 inputPinIndex < loadedComponentChip.inputPins.Length &&
                 inputPinIndex <
                     chipToLoad.savedComponentChips[chipIndex].inputPins.Length;
                 inputPinIndex++)
            {
                SavedInputPin savedPin =
                    chipToLoad.savedComponentChips[chipIndex].inputPins[inputPinIndex];
                Pin pin = loadedComponentChip.inputPins[inputPinIndex];

                // If this pin should receive input from somewhere, then wire it up to
                // that pin
                if (savedPin.parentChipIndex != -1)
                {
                    Pin connectedPin =
                        loadedChipData.componentChips[savedPin.parentChipIndex]
                            .outputPins[savedPin.parentChipOutputIndex];
                    pin.cyclic = savedPin.isCylic;
                    Pin.TryConnect(connectedPin, pin);
                }
            }
        }

        return loadedChipData;
    }

    static ChipSaveData LoadChipWithWires(SavedChip chipToLoad, Wire wirePrefab, ChipEditor chipEditor)
    {
        var previouslyLoadedChips = Manager.instance.AllSpawnableChipDic();
        ChipSaveData loadedChipData = new ChipSaveData();
        int numComponents = chipToLoad.savedComponentChips.Length;
        loadedChipData.componentChips = new Chip[numComponents];
        loadedChipData.Data = chipToLoad.Data;
        List<Wire> wiresToLoad = new List<Wire>();

        // Spawn component chips (the chips used to create this chip)
        // These will have been loaded already, and stored in the
        // previouslyLoadedChips dictionary
        for (int i = 0; i < numComponents; i++)
        {
            SavedComponentChip componentToLoad = chipToLoad.savedComponentChips[i];
            string componentName = componentToLoad.chipName;
            Vector2 pos = new Vector2((float)componentToLoad.posX, (float)componentToLoad.posY);

            if (!previouslyLoadedChips.ContainsKey(componentName))
                DLSLogger.LogError($"Failed to load sub component: {componentName} While loading {chipToLoad.Data.name}");

            Chip loadedComponentChip = GameObject.Instantiate(previouslyLoadedChips[componentName], pos, Quaternion.identity, chipEditor.chipImplementationHolder);

            loadedComponentChip.gameObject.SetActive(true);
            loadedChipData.componentChips[i] = loadedComponentChip;

            // Load input pin names
            for (int inputIndex = 0;
                 inputIndex < componentToLoad.inputPins.Length &&
                 inputIndex < loadedChipData.componentChips[i].inputPins.Length;
                 inputIndex++)
            {
                loadedChipData.componentChips[i].inputPins[inputIndex].pinName =
                    componentToLoad.inputPins[inputIndex].name;
                loadedChipData.componentChips[i].inputPins[inputIndex].wireType =
                    componentToLoad.inputPins[inputIndex].wireType;
            }

            // Load output pin names
            for (int ouputIndex = 0;
                 ouputIndex < componentToLoad.outputPins.Length &&
                 ouputIndex < loadedChipData.componentChips[i].outputPins.Length;
                 ouputIndex++)
            {
                loadedChipData.componentChips[i].outputPins[ouputIndex].pinName =
                    componentToLoad.outputPins[ouputIndex].name;
                loadedChipData.componentChips[i].outputPins[ouputIndex].wireType =
                    componentToLoad.outputPins[ouputIndex].wireType;
            }
        }

        // Connect pins with wires
        for (int chipIndex = 0; chipIndex < chipToLoad.savedComponentChips.Length;
             chipIndex++)
        {
            Chip loadedComponentChip = loadedChipData.componentChips[chipIndex];
            for (int inputPinIndex = 0;
                 inputPinIndex < loadedComponentChip.inputPins.Length &&
                 inputPinIndex < chipToLoad.savedComponentChips[chipIndex].inputPins.Length;
                 inputPinIndex++)
            {
                SavedInputPin savedPin =
                    chipToLoad.savedComponentChips[chipIndex].inputPins[inputPinIndex];
                Pin pin = loadedComponentChip.inputPins[inputPinIndex];

                // If this pin should receive input from somewhere, then wire it up to
                // that pin
                if (savedPin.parentChipIndex != -1)
                {
                    Pin connectedPin =
                        loadedChipData.componentChips[savedPin.parentChipIndex]
                            .outputPins[savedPin.parentChipOutputIndex];
                    pin.cyclic = savedPin.isCylic;
                    if (Pin.TryConnect(connectedPin, pin))
                    {
                        Wire loadedWire = GameObject.Instantiate(wirePrefab, chipEditor.wireHolder);
                        loadedWire.Connect(connectedPin, pin);
                        wiresToLoad.Add(loadedWire);
                    }
                }
            }
        }

        loadedChipData.wires = wiresToLoad.ToArray();

        return loadedChipData;
    }

    public static ChipSaveData GetChipSaveData(Chip chip, Wire wirePrefab, ChipEditor chipEditor)
    {
        // @NOTE: chipEditor can be removed here if:
        //     * Chip & wire instatiation is inside their respective implementation
        //     holders is inside the chipEditor
        //     * the wire connections are done inside ChipEditor.LoadFromSaveData
        //     instead of ChipLoader.LoadChipWithWires

        SavedChip chipToTryLoad = SaveSystem.ReadChip(chip.chipName);

        if (chipToTryLoad == null)
            return null;

        ChipSaveData loadedChipData = LoadChipWithWires(chipToTryLoad, wirePrefab, chipEditor);
        SavedWireLayout wireLayout = SaveSystem.ReadWire(loadedChipData.Data.name);

        //Work Around solution. it just Work but maybe is worth to change the entire way to save WireLayout (idk i don't think so)
        for (int i = 0; i < loadedChipData.wires.Length; i++)
        {
            Wire wire = loadedChipData.wires[i];
            wire.endPin.pinName = wire.endPin.pinName+i;
        }    

        // Set wires anchor points
        foreach (SavedWire wire in wireLayout.serializableWires)
        {
            string startPinName;
            string endPinName;

            // This fixes a bug which caused chips to be unable to be viewed/edited if
            // some of input/output pins were swaped.
            try
            {
                startPinName = loadedChipData.componentChips[wire.parentChipIndex]
                                   .outputPins[wire.parentChipOutputIndex]
                                   .pinName;
                endPinName = loadedChipData.componentChips[wire.childChipIndex]
                                 .inputPins[wire.childChipInputIndex]
                                 .pinName;
            }
            catch (IndexOutOfRangeException)
            {
                // Swap input pins with output pins.
                startPinName = loadedChipData.componentChips[wire.parentChipIndex]
                                   .inputPins[wire.parentChipOutputIndex]
                                   .pinName;
                endPinName = loadedChipData.componentChips[wire.childChipIndex]
                                 .outputPins[wire.childChipInputIndex]
                                 .pinName;
            }
            int wireIndex = Array.FindIndex(loadedChipData.wires, w => w.startPin.pinName == startPinName && w.endPin.pinName == endPinName);
            if (wireIndex >= 0)
                loadedChipData.wires[wireIndex].SetAnchorPoints(wire.anchorPoints);
        }

        for (int i = 0; i < loadedChipData.wires.Length; i++)
        {
            Wire wire = loadedChipData.wires[i];
            wire.endPin.pinName = wire.endPin.pinName.Remove(wire.endPin.pinName.Length - 1);
        }

        return loadedChipData;
    }

    public static void Import(string path)
    {
        var allChips = SaveSystem.GetAllSavedChips();
        var nameUpdateLookupTable = new Dictionary<string, string>();

        using var reader = new StreamReader(path);
        var numberOfChips = Int32.Parse(reader.ReadLine());

        for (var i = 0; i < numberOfChips; i++)
        {
            string chipName = reader.ReadLine();
            int saveDataLength = Int32.Parse(reader.ReadLine());
            int wireSaveDataLength = Int32.Parse(reader.ReadLine());

            string saveData = "";
            string wireSaveData = "";

            for (int j = 0; j < saveDataLength; j++)
            {
                saveData += reader.ReadLine() + "\n";
            }
            for (int j = 0; j < wireSaveDataLength; j++)
            {
                wireSaveData += reader.ReadLine() + "\n";
            }

            // Rename chip if already exist
            if (Array.FindIndex(allChips, c => c.Data.name == chipName) >= 0)
            {
                int nameCounter = 2;
                string newName;
                do
                {
                    newName = chipName + nameCounter.ToString();
                    nameCounter++;
                } while (Array.FindIndex(allChips, c => c.Data.name == newName) >= 0);

                nameUpdateLookupTable.Add(chipName, newName); chipName = newName;
            }

            // Update name inside file if there was some names changed
            foreach (KeyValuePair<string, string> nameToReplace in nameUpdateLookupTable)
            {
                saveData = saveData
                    .Replace("\"name\": \"" + nameToReplace.Key + "\"",
                        "\"name\": \"" + nameToReplace.Value + "\"")
                    .Replace("\"chipName\": \"" + nameToReplace.Key + "\"",
                        "\"chipName\": \"" + nameToReplace.Value + "\"");
            }

            string chipSaveFile = SaveSystem.GetPathToSaveFile(chipName);

            SaveSystem.WriteChip(chipName, saveData);
            SaveSystem.WriteWire(chipName, wireSaveData);
        }
    }
}
