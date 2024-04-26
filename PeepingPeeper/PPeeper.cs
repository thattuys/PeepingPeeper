using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.GeneratedSheets;
//using Lumina.Excel.GeneratedSheets2;
using PeepingTom.Ipc;
using PeepingTom.Ipc.From;
using PeepingTom.Ipc.To;

namespace PeepingPeeper;

public class PPeeper
{
    public class Plugin : IDalamudPlugin
    {
        internal static string Name => "Peeping Peeper";

        [PluginService]
        internal DalamudPluginInterface Interface { get; init; } = null!;
        [PluginService]
        internal ITargetManager TargetManager { get; init; } = null!;
        [PluginService]
        internal IObjectTable ObjectTable { get; init; } = null!;
        [PluginService]
        internal IClientState ClientState { get; init; } = null!;
        [PluginService]
        internal ICommandManager CommandManager { get; init; } = null!;
        [PluginService]
        internal IDataManager DataManager { get; init; } = null!;
        [PluginService]
        internal IPluginLog PluginLog { get; init; } = null!;

        private Configuration Configuration { get; init; } = null!;
        private ICallGateSubscriber<IFromMessage, object> Subscriber { get; }
        private ICallGateProvider<IToMessage, object> Provider { get; }

        private bool Enabled = false;

        public Plugin(
            [RequiredVersion("1.0")] DalamudPluginInterface pluginInterface,
            [RequiredVersion("1.0")] ITargetManager targetManager,
            [RequiredVersion("1.0")] IObjectTable objectTable,
            [RequiredVersion("1.0")] IClientState clientState,
            [RequiredVersion("1.0")] ICommandManager commandManager,
            [RequiredVersion("1.0")] IDataManager dataManager,
            [RequiredVersion("1.0")] IPluginLog pluginLog
        )
        {
            this.Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            this.Configuration.Initialize(pluginInterface);

            this.Interface = pluginInterface;
            this.TargetManager = targetManager;
            this.ObjectTable = objectTable;
            this.ClientState = clientState;
            this.CommandManager = commandManager;
            this.DataManager = dataManager;
            this.PluginLog = pluginLog;

            commandManager.AddHandler("/ppeeperignore", new CommandInfo(IgnoreCurrentTarget)
            {
                HelpMessage = "Ignore this person staring at you."
            });
            commandManager.AddHandler("/ppeeperunignore", new CommandInfo(UnignoreCurrentTarget)
            {
                HelpMessage = "Start staring this creep back down :3."
            });
            commandManager.AddHandler("/ppeepertogglesf", new CommandInfo(SoftTargetToggle)
            {
                HelpMessage = "Toggle using softtarget."
            });

            this.Subscriber = pluginInterface.GetIpcSubscriber<IFromMessage, object>(IpcInfo.FromRegistrationName);
            this.Provider = pluginInterface.GetIpcProvider<IToMessage, object>(IpcInfo.ToRegistrationName);
            this.Subscriber.Subscribe(this.ReceiveMessage);

            this.ClientState.TerritoryChanged += this.OnTerritoryChange;
            Enabled = true;

            Configuration.Save();
        }

        private void ReceiveMessage(IFromMessage message)
        {
            if (message.GetType() == typeof(NewTargeterMessage))
            {
                var targetMessage = (NewTargeterMessage)message;
                foreach (var player in Configuration.Players)
                {
                    if (player.PlayerName == targetMessage.Targeter.Name.ToString() && player.Homeworld == targetMessage.Targeter.HomeWorldId)
                        return;
                }
                var target = ObjectTable.FirstOrDefault(a => a.ObjectId == targetMessage.Targeter.ObjectId);
                if (target!.TargetObjectId == ClientState.LocalPlayer!.ObjectId)
                    SetTarget(target);
                else if (ClientState.LocalPlayer!.TargetObjectId == target!.ObjectId)
                    Provider.SendMessage(new RequestTargetersMessage());
            }
            else if (message.GetType() == typeof(AllTargetersMessage))
            {
                var targets = (AllTargetersMessage)message;

                Targeter? oldestValidTarget = null;
                foreach (var (targeter, currentlyTargeting) in targets.Targeters)
                {
                    if (!currentlyTargeting) continue;

                    var blockedPlayer = Configuration.Players.Find(x => x.PlayerName == targeter.Name.ToString() && x.Homeworld == targeter.HomeWorldId);
                    if (blockedPlayer == null)
                    {
                        if (targeter.GetPlayerCharacter(ObjectTable)!.TargetObjectId != ClientState.LocalPlayer!.ObjectId) continue;
                        if (oldestValidTarget == null || oldestValidTarget.When < targeter.When)
                            oldestValidTarget = targeter;
                    }
                }

                SetTarget(oldestValidTarget != null ? oldestValidTarget.GetPlayerCharacter(ObjectTable) : null);
            }
        }

        public void SetTarget(GameObject? target)
        {
            if (Configuration.UseSoftTarget)
                TargetManager.SoftTarget = target;
            else
                TargetManager.Target = target;
        }

        public void IgnoreCurrentTarget(string command, string args)
        {
            if (ClientState.LocalPlayer == null)
                return;

            var target = ObjectTable.FirstOrDefault(a => a.ObjectId == ClientState.LocalPlayer.TargetObjectId) as PlayerCharacter;
            if (target == null)
                return;

            var player = new PlayerList
            {
                PlayerName = target.Name.ToString(),
                Homeworld = target.HomeWorld.Id
            };

            Configuration.Players.Add(player);
            Configuration.Save();
        }

        public void UnignoreCurrentTarget(string command, string args)
        {
            if (ClientState.LocalPlayer == null)
                return;

            var target = ObjectTable.FirstOrDefault(a => a.ObjectId == ClientState.LocalPlayer.TargetObjectId) as PlayerCharacter;
            if (target == null)
                return;

            foreach (var player in Configuration.Players)
                if (player.PlayerName == target.Name.ToString() && player.Homeworld == target.HomeWorld.Id)
                    Configuration.Players.Remove(player);

            Configuration.Save();
        }

        private void OnTerritoryChange(ushort e) {
            try {
                var territory = this.DataManager.GetExcelSheet<TerritoryType>()!.GetRow(e);
                if (territory == null) return;

                if (territory.TerritoryIntendedUse <= 2  && Enabled == false) {
                    this.Subscriber.Subscribe(this.ReceiveMessage);
                    PluginLog.Debug("Enabling peeping peeper");
                    Enabled = true;
                } else if (territory.TerritoryIntendedUse >= 3 && Enabled != false) {
                    this.Subscriber.Unsubscribe(this.ReceiveMessage);
                    PluginLog.Debug("Disabling peeping peeper");
                    Enabled = false;
                }

                
            } catch (Exception ex) {
                PluginLog.Error(ex.ToString());
            }
        }
        public void SoftTargetToggle(string command, string args)
        {
            Configuration.UseSoftTarget = !Configuration.UseSoftTarget;
            Configuration.Save();
        }

        public void Dispose() => Subscriber.Unsubscribe(ReceiveMessage);
    }
}
