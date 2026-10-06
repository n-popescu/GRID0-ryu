using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ryujinx.Ava.Systems.Grid0;
using Ryujinx.HLE.HOS.Services.Account.Acc.SwitchNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Windows
{
    /// <summary>
    /// GRID0+ → Friends: this player's friend code, their friends and what each is playing,
    /// adding a friend by code and answering requests. The same window citron's GRID0+ build has.
    /// </summary>
    /// <remarks>
    /// Not modal, so it can stay open beside a running game; it refreshes itself every
    /// <see cref="_refreshInterval"/>, which is about how quickly a friend coming online shows.
    /// </remarks>
    public sealed class Grid0FriendsWindow : StyleableWindow
    {
        private static readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(20);

        private static Grid0FriendsWindow _open;

        private readonly TextBlock _me = new() { FontSize = 16, FontWeight = FontWeight.SemiBold };
        private readonly TextBlock _status = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel _friends = new() { Spacing = 6 };
        private readonly StackPanel _incoming = new() { Spacing = 6 };
        private readonly StackPanel _outgoing = new() { Spacing = 6 };
        private readonly TextBox _code = new() { PlaceholderText = "SW-0000-0000-0000", Width = 220 };
        private readonly CancellationTokenSource _cts = new();
        private readonly Dictionary<ulong, Bitmap> _images = [];
        private readonly DispatcherTimer _timer;

        private bool _busy;

        public static void ShowOrRaise()
        {
            if (_open != null)
            {
                _open.Activate();

                return;
            }

            _open = new Grid0FriendsWindow();
            _open.Closed += (_, _) => _open = null;
            _open.Show(RyujinxApp.MainWindow);
        }

        private Grid0FriendsWindow()
        {
            Title = "GRID0+ Friends";
            Width = 460;
            Height = 620;
            MinWidth = 380;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            Button add = new() { Content = "Send request" };
            add.Click += async (_, _) => await RunAsync(async c =>
            {
                string code = _code.Text?.Trim() ?? "";

                if (code.Length == 0)
                {
                    return "Enter a friend code.";
                }

                string problem = await Grid0Friends.SendRequestAsync(c, code, _cts.Token);

                if (problem == null)
                {
                    _code.Text = "";
                }

                return problem ?? "Friend request sent.";
            });

            Button refresh = new() { Content = "Refresh" };
            refresh.Click += async (_, _) => await RefreshAsync();

            StackPanel content = new()
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    _me,
                    _status,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _code, add, refresh } },
                    Header("Friends"),
                    _friends,
                    Header("Requests received"),
                    _incoming,
                    Header("Requests sent"),
                    _outgoing,
                },
            };

            Content = new ScrollViewer { Content = content };

            _timer = new DispatcherTimer { Interval = _refreshInterval };
            _timer.Tick += async (_, _) => await RefreshAsync();

            Opened += async (_, _) =>
            {
                _timer.Start();
                await RefreshAsync();
            };

            Closed += (_, _) =>
            {
                _timer.Stop();
                _cts.Cancel();
            };
        }

        private static TextBlock Header(string text) =>
            new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) };

        /// <summary>Runs one action against the server, then shows its message and a fresh list.</summary>
        private async Task RunAsync(Func<SwitchNetAccountClient, Task<string>> action)
        {
            SwitchNetAccountClient client = Grid0Account.Client();

            if (client == null)
            {
                _status.Text = NotConfigured;

                return;
            }

            string message;

            try
            {
                message = await Task.Run(() => action(client));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                message = e.Message;
            }

            await RefreshAsync();

            if (message != null)
            {
                _status.Text = message;
            }
        }

        private const string NotConfigured =
            "No GRID0+ login. Set it in Settings → Network → GRID0+ with the login the GRID0+ Discord bot gave you (/register).";

        private async Task RefreshAsync()
        {
            if (_busy)
            {
                return;
            }

            SwitchNetAccountClient client = Grid0Account.Client();

            if (client == null)
            {
                _me.Text = "Not signed in";
                _status.Text = NotConfigured;

                return;
            }

            _busy = true;

            try
            {
                Grid0Friend me = null;
                List<Grid0Friend> friends;
                List<Grid0FriendRequest> incoming;
                List<Grid0FriendRequest> outgoing;

                try
                {
                    (me, friends, incoming, outgoing) = await Task.Run(async () =>
                    {
                        Grid0Friend m = await Grid0Friends.FetchMeAsync(client, _cts.Token);
                        List<Grid0Friend> f = await Grid0Friends.FetchFriendsAsync(client, _cts.Token);
                        (List<Grid0FriendRequest> i, List<Grid0FriendRequest> o) = await Grid0Friends.FetchRequestsAsync(client, _cts.Token);

                        return (m, f, i, o);
                    });
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    _status.Text = $"Could not reach the GRID0+ server: {e.Message}";

                    return;
                }

                // Opening the friend list is being online, as on a console's Home Menu; a
                // running game has already reported something more specific, which this keeps.
                if (RyujinxApp.MainWindow?.ViewModel?.IsGameRunning != true)
                {
                    Grid0Friends.SetPresence(Grid0Account.Client, 0);
                }

                _me.Text = $"{me.Nickname}   ·   {(string.IsNullOrEmpty(me.FriendCode) ? "no friend code" : me.FriendCode)}";

                int online = friends.Count(f => f.State is "ONLINE" or "PLAYING");
                _status.Text = $"{friends.Count} friend(s), {online} online.";

                _friends.Children.Clear();
                foreach (Grid0Friend friend in friends
                    .OrderByDescending(f => f.State == "PLAYING")
                    .ThenByDescending(f => f.State == "ONLINE")
                    .ThenBy(f => f.Nickname, StringComparer.OrdinalIgnoreCase))
                {
                    _friends.Children.Add(FriendRow(client, friend));
                }

                if (friends.Count == 0)
                {
                    _friends.Children.Add(new TextBlock { Text = "No friends yet.", Opacity = 0.6 });
                }

                _incoming.Children.Clear();
                foreach (Grid0FriendRequest request in incoming)
                {
                    _incoming.Children.Add(RequestRow(request, ("Accept", "accept"), ("Decline", "deny")));
                }

                if (incoming.Count == 0)
                {
                    _incoming.Children.Add(new TextBlock { Text = "None.", Opacity = 0.6 });
                }

                _outgoing.Children.Clear();
                foreach (Grid0FriendRequest request in outgoing)
                {
                    _outgoing.Children.Add(RequestRow(request, ("Cancel", "cancel")));
                }

                if (outgoing.Count == 0)
                {
                    _outgoing.Children.Add(new TextBlock { Text = "None.", Opacity = 0.6 });
                }
            }
            finally
            {
                _busy = false;
            }
        }

        private Control FriendRow(SwitchNetAccountClient client, Grid0Friend friend)
        {
            (string text, IBrush color) = friend.State switch
            {
                "PLAYING" => ($"Playing {TitleName(friend.AppId)}", Brushes.LimeGreen),
                "ONLINE" => ("Online", Brushes.DeepSkyBlue),
                "OFFLINE" => ("Offline", Brushes.Gray),
                _ => ("Presence hidden", Brushes.Gray),
            };

            Image avatar = new() { Width = 40, Height = 40 };
            _ = LoadImageAsync(client, friend, avatar);

            Button remove = new() { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
            remove.Click += async (_, _) =>
                await RunAsync(async c => await Grid0Friends.RemoveFriendAsync(c, friend.NsaId, _cts.Token) ?? $"Removed {friend.Nickname}.");

            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            StackPanel text_ = new()
            {
                Margin = new Thickness(10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = (friend.IsFavorite ? "★ " : "") + friend.Nickname, FontWeight = FontWeight.SemiBold },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Children =
                        {
                            new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = color, VerticalAlignment = VerticalAlignment.Center },
                            new TextBlock { Text = text, Opacity = 0.8 },
                        },
                    },
                },
            };

            Grid.SetColumn(avatar, 0);
            Grid.SetColumn(text_, 1);
            Grid.SetColumn(remove, 2);
            row.Children.Add(avatar);
            row.Children.Add(text_);
            row.Children.Add(remove);

            return row;
        }

        private Control RequestRow(Grid0FriendRequest request, params (string Label, string Action)[] actions)
        {
            StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

            string who = request.Other.Nickname;
            if (!string.IsNullOrEmpty(request.Other.FriendCode))
            {
                who += $" ({request.Other.FriendCode})";
            }

            row.Children.Add(new TextBlock { Text = who, VerticalAlignment = VerticalAlignment.Center, MinWidth = 200 });

            foreach ((string label, string action) in actions)
            {
                Button button = new() { Content = label };
                button.Click += async (_, _) =>
                    await RunAsync(async c => await Grid0Friends.SettleRequestAsync(c, request.Id, action, _cts.Token));
                row.Children.Add(button);
            }

            return row;
        }

        private async Task LoadImageAsync(SwitchNetAccountClient client, Grid0Friend friend, Image target)
        {
            if (_images.TryGetValue(friend.NsaId, out Bitmap cached))
            {
                target.Source = cached;

                return;
            }

            if (string.IsNullOrEmpty(friend.ImageUrl))
            {
                return;
            }

            try
            {
                byte[] jpeg = await Task.Run(() => Grid0Friends.FetchImageAsync(client, friend.ImageUrl, _cts.Token));

                if (jpeg == null)
                {
                    return;
                }

                Bitmap bitmap = new(new MemoryStream(jpeg));
                _images[friend.NsaId] = bitmap;
                target.Source = bitmap;
            }
            catch (Exception)
            {
                // A missing picture is not worth a message; the row still says who it is.
            }
        }

        /// <summary>The game's name when it is in the library, its title id otherwise.</summary>
        private static string TitleName(string appId)
        {
            if (string.IsNullOrEmpty(appId))
            {
                return "a game";
            }

            if (ulong.TryParse(appId, System.Globalization.NumberStyles.HexNumber, null, out ulong id))
            {
                string name = RyujinxApp.MainWindow?.ViewModel?.Applications?
                    .FirstOrDefault(a => a.Id == id)?.Name;

                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }

            return appId;
        }
    }
}
