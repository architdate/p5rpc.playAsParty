using CriFs.V2.Hook.Interfaces;
using CriFsV2Lib.Definitions;
using p5rpc.flowscriptframework.interfaces;
using p5rpc.playAsParty.Configuration;
using p5rpc.playAsParty.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Memory.Sigscan.Definitions;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Memory;
using Reloaded.Mod.Interfaces;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;
using p5rpc.lib.interfaces;

namespace p5rpc.playAsParty
{
    /// <summary>
    /// Your mod logic goes here.
    /// </summary>
    public class Mod : ModBase // <= Do not Remove.
    {
        /// <summary>
        /// Provides access to the mod loader API.
        /// </summary>
        private readonly IModLoader _modLoader;

        /// <summary>
        /// Provides access to the Reloaded.Hooks API.
        /// </summary>
        /// <remarks>This is null if you remove dependency on Reloaded.SharedLib.Hooks in your mod.</remarks>
        private readonly IReloadedHooks? _hooks;

        /// <summary>
        /// Provides access to the Reloaded logger.
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Entry point into the mod, instance that created this class.
        /// </summary>
        private readonly IMod _owner;

        /// <summary>
        /// Provides access to this mod's configuration.
        /// </summary>
        private Config _configuration;

        /// <summary>
        /// The configuration of the currently executing mod.
        /// </summary>
        private readonly IModConfig _modConfig;

        private Memory _memory;

        private nuint _expMultiplier;

        private IAsmHook _expGainHook;
        private IP5RLib p5rLib;

        private delegate void UpdatePartyPostBattle(nint param1);
        private IHook<UpdatePartyPostBattle>? updateBattleHook;

        private nint _DAT_29e8c00_Address;

        public Mod(ModContext context)
        {
            _modLoader = context.ModLoader;
            _hooks = context.Hooks;
            _logger = context.Logger;
            _owner = context.Owner;
            _configuration = context.Configuration;
            _modConfig = context.ModConfig;

            Utils.Initialise(_logger, _configuration);

            var startupScannerController = _modLoader.GetController<IStartupScanner>();
            if (startupScannerController == null || !startupScannerController.TryGetTarget(out var startupScanner))
            {
                Utils.LogError($"Unable to get controller for Reloaded SigScan Library, aborting initialisation");
                return;
            }

            var criFsController = _modLoader.GetController<ICriFsRedirectorApi>();
            if (criFsController == null || !criFsController.TryGetTarget(out var criFsApi))
            {
                Utils.LogError($"Could not hook to crifs lib");
                return;
            }

            var flowFrameworkController = _modLoader.GetController<IFlowFramework>();
            if (flowFrameworkController == null || !flowFrameworkController.TryGetTarget(out var flowFramework))
            {
                Utils.LogError($"Could not load IFlowFramework");
                return;
            }

            var p5rLibController = _modLoader.GetController<IP5RLib>();
            if (p5rLibController == null || !p5rLibController.TryGetTarget(out p5rLib))
            {
                Utils.LogError($"Could not load IP5RLib");
                return;
            }

            else
            {
                switch (_configuration.Player)
                {
                    case Config.Character.Joker:
                        break;
                    case Config.Character.Akechi:
                        criFsApi.AddProbingPath(Path.Combine("Party", "Akechi"));
                        break;
                    case Config.Character.Sumire:
                        criFsApi.AddProbingPath(Path.Combine("Party", "Sumire"));
                        break;
                }
            }

            _memory = new Memory();
            _expMultiplier = _memory.Allocate(4).Address;
            _memory.Write(_expMultiplier, _configuration.ExpMultiplier);

            startupScanner.AddMainModuleScan("45 31 C9 48 8D 15 ?? ?? ?? ?? 45 89 C8", result =>{
                Utils.LogDebug($"Found update battle hook at at 0x{Utils.BaseAddress + result.Offset:X}");
                updateBattleHook = _hooks.CreateHook<UpdatePartyPostBattle>(UpdatePartyPostBattleImpl, result.Offset + Utils.BaseAddress).Activate();
            });

            startupScanner.AddMainModuleScan("8B F8 33 DB 0F B7 84 24 ?? ?? ?? ??", result =>
            {
                if (!result.Found)
                {
                    Utils.LogError($"Unable to find address for Persona exp gain, aborting initialisation :(");
                    return;
                }
                Utils.LogDebug($"Found Persona exp gain at 0x{Utils.BaseAddress + result.Offset:X}");

                string[] function =
                {
                    "use64",
                    $"mulss xmm0, [qword {_expMultiplier}]",
                    "cvttss2si eax, xmm0"
                };
                _expGainHook = _hooks.CreateAsmHook(function, result.Offset + Utils.BaseAddress, AsmHookBehaviour.ExecuteFirst).Activate();
            });

            startupScanner.AddMainModuleScan("48 8D 05 ?? ?? ?? ?? 81 0C ?? 00 40 00 00", result =>
            {
                if (!result.Found)
                {
                    Utils.LogError($"Unable to find address for DAT_29e8c00, aborting initialisation :(");
                    return;
                }

                var offsetAddress = Utils.BaseAddress + result.Offset + 3; // 48 8d 05 (64 bit instruction, LEA, RAX)
                unsafe
                {
                    var offsetAddressPtr = (int*)offsetAddress;
                    var offset = *offsetAddressPtr;
                    var instruction_length = 7; // 48 8d 05 54 ca 2c 02
                    _DAT_29e8c00_Address = Utils.BaseAddress + result.Offset + instruction_length + offset + 0x2A0; // No idea why 2A0 is needed
                    Utils.LogDebug($"_DAT_29e8c00_Address: 0x{_DAT_29e8c00_Address:X}");
                }

            });

            var id_trait = flowFramework.Register("SET_EQUIP_PERSONA_TRAIT", 2, () =>
            {
                var api = flowFramework.GetFlowApi();
                var num = api.GetIntArg(0);
                var party_id = api.GetIntArg(1);
                if ((int)_configuration.Player != party_id)
                    return FlowStatus.SUCCESS;
                unsafe
                {
                    var equipped_persona = *(ushort*)(_DAT_29e8c00_Address + 0x40);
                    *(ushort*)(_DAT_29e8c00_Address + 0x4A + (0x30 * equipped_persona)) = (ushort)num;
                }
                return FlowStatus.SUCCESS;
            });

            var id_species = flowFramework.Register("SET_EQUIP_PERSONA_ID", 2, () =>
            {
                var api = flowFramework.GetFlowApi();
                var num = api.GetIntArg(0);
                var party_id = api.GetIntArg(1);
                if ((int)_configuration.Player != party_id)
                    return FlowStatus.SUCCESS;
                unsafe
                {
                    var equipped_persona = *(ushort*)(_DAT_29e8c00_Address + 0x40);
                    *(ushort*)(_DAT_29e8c00_Address + 0x46 + (0x30 * equipped_persona)) = (ushort)num;
                }
                return FlowStatus.SUCCESS;
            });

            var party_persona = flowFramework.Register("ADD_STARTING_PERSONA", 0, () =>
            {
                switch (_configuration.Player)
                {
                    case Config.Character.Joker:
                        p5rLib.FlowCaller.ADD_PERSONA_STOCK(201);
                        break;
                    case Config.Character.Akechi:
                        p5rLib.FlowCaller.ADD_PERSONA_STOCK(209);
                        break;
                    case Config.Character.Sumire:
                        p5rLib.FlowCaller.ADD_PERSONA_STOCK(240);
                        break;
                }
                return FlowStatus.SUCCESS;
            });

            Utils.LogDebug($"Flow Framework ID registered as: {id_trait} for trait");
            Utils.LogDebug($"Flow Framework ID registered as: {id_species} for species");
            Utils.LogDebug($"Flow Framework ID registered as: {party_persona} for starting persona stock add");

            // For more information about this template, please see
            // https://reloaded-project.github.io/Reloaded-II/ModTemplate/

            // If you want to implement e.g. unload support in your mod,
            // and some other neat features, override the methods in ModBase.

            // TODO: Implement some mod logic
        }

        private void UpdatePartyPostBattleImpl(nint param1)
        {
            updateBattleHook!.OriginalFunction(param1);
            p5rLib.FlowCaller.SET_EQUIP(1,3,28968);
        }

        #region Standard Overrides
        public override void ConfigurationUpdated(Config configuration)
        {
            // Apply settings from configuration.
            // ... your code here.
            _configuration = configuration;
            _logger.WriteLine($"[{_modConfig.ModId}] Config Updated: Applying");
        }
        #endregion

        #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public Mod() { }
#pragma warning restore CS8618
        #endregion
    }
}