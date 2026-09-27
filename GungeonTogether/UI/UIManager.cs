using System.Collections.Generic;
using UnityEngine;
using GungeonTogether.Networking.Session;
using GungeonTogether.Networking.Transport;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.UI
{
	/// <summary>
	/// The multiplayer menu, toggled with Ctrl+P. Drawn with Unity's immediate-mode GUI (OnGUI)
	/// rather than the game's df* controls: df controls added at runtime never received mouse
	/// clicks, while IMGUI reads the mouse directly and needs no scene setup.
	/// </summary>
	public static class UIManager
	{
		// The menu is laid out at this height and scaled to the screen, so it keeps its size at any resolution.
		private const float ReferenceHeight = 720f;
		private const float PanelWidth = 320f;
		private const string InputOverrideKey = "GungeonTogether.Menu";

		private static bool _visible;
		private static PlayerController _blockedPlayer;
		private static Rect _panelRect = new Rect(20f, 20f, PanelWidth, 0f);
		private static readonly List<string> _memberNames = new List<string>();
		private static bool _memberNamesDirty = true;

		public static bool IsVisible => _visible;

		public static void Initialise()
		{
			SteamLobby.Instance.OnPlayerListChanged += () => _memberNamesDirty = true;
		}

		public static void Update()
		{
			bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
			if (ctrl && Input.GetKeyDown(KeyCode.P))
			{
				_visible = !_visible;
				_memberNamesDirty = true;
				Debug.Log($"[UI] Menu {(_visible ? "opened" : "closed")}.");
			}

			UpdatePlayerInputBlock();
		}

		/// <summary>Called from GungeonTogetherMod.OnGUI.</summary>
		public static void OnGUI()
		{
			if (!_visible) return;

			float scale = Mathf.Max(1f, Screen.height / ReferenceHeight);
			Matrix4x4 previous = GUI.matrix;
			GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
			try
			{
				_panelRect = GUILayout.Window(0x6754, _panelRect, DrawWindow, "Gungeon Together", GUILayout.Width(PanelWidth));
			}
			finally
			{
				GUI.matrix = previous;
			}
		}

		private static void DrawWindow(int id)
		{
			var session = NetworkSession.Instance;
			var lobby = SteamLobby.Instance;

			if (!lobby.IsInitialised)
			{
				GUILayout.Label("Waiting for Steam...");
			}
			else
			{
				string role = session.IsHost ? "Host" : (session.IsClient ? "Client" : "(none)");
				GUILayout.Label(lobby.IsInLobby ? $"Lobby: {lobby.CurrentLobbyId}" : "Lobby: (none)");
				GUILayout.Label($"Role: {role} | Net: {(session.IsConnected ? "Connected" : "Disconnected")}");
				string ping = session.GetPingText();
				if (ping.Length > 0) GUILayout.Label(ping);
			}

			GUILayout.Space(6f);
			GUILayout.BeginHorizontal();
			GUI.enabled = lobby.IsInitialised && !lobby.IsInLobby && !session.IsConnected;
			if (GUILayout.Button("Host Lobby")) OnHostClicked();
			GUI.enabled = lobby.IsInLobby;
			if (GUILayout.Button("Invite")) OnInviteClicked();
			GUI.enabled = lobby.IsInLobby || session.IsConnected;
			if (GUILayout.Button("Leave")) OnLeaveClicked();
			GUI.enabled = true;
			GUILayout.EndHorizontal();

			if (lobby.IsInLobby)
			{
				GUILayout.Space(6f);
				GUILayout.Label("Players:");
				RefreshMemberNames();
				foreach (string name in _memberNames)
				{
					GUILayout.Label("  " + name);
				}
			}

			GUILayout.Space(4f);
			GUILayout.Label("Ctrl+P to close");
			GUI.DragWindow();
		}

		private static void RefreshMemberNames()
		{
			if (!_memberNamesDirty) return;
			_memberNamesDirty = false;
			_memberNames.Clear();
			foreach (ulong id in SteamLobby.Instance.GetLobbyMembers())
			{
				_memberNames.Add(SteamIdentity.GetPlayerName(id));
			}
		}

		/// <summary>
		/// Stops the local player from shooting/moving while the menu is open, so clicking a button
		/// doesn't also fire. Follows the PlayerController across level loads.
		/// </summary>
		private static void UpdatePlayerInputBlock()
		{
			PlayerController player = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
			PlayerController wanted = _visible ? player : null;
			if (wanted == _blockedPlayer) return;

			if (_blockedPlayer != null) _blockedPlayer.ClearInputOverride(InputOverrideKey);
			if (wanted != null) wanted.SetInputOverride(InputOverrideKey);
			_blockedPlayer = wanted;
		}

		private static void OnHostClicked()
		{
			Debug.Log("[UI] Host Lobby clicked.");
			SteamLobby.Instance.CreateLobby(4);
		}

		private static void OnInviteClicked()
		{
			Debug.Log("[UI] Invite clicked.");
			SteamLobby.Instance.OpenInviteDialog();
		}

		private static void OnLeaveClicked()
		{
			Debug.Log("[UI] Leave clicked.");
			SteamLobby.Instance.LeaveLobby();
			NetworkSession.Instance.Shutdown();
		}
	}
}
