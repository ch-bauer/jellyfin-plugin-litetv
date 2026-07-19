(function () {
    'use strict';

    // LiteTV Channels: lightweight virtual TV channels for the Jellyfin web client.
    // Injected by the LiteTV Channels plugin. Renders a channel guide (home row +
    // header button), tunes into channels via normal direct playback at the live
    // position, and drives the end-of-episode continue/schedule flow.

    var HOME_ROW_ID = 'liteTvHomeRow';
    var GUIDE_ID = 'liteTvGuide';
    var STYLE_ID = 'liteTvStyle';
    var TUNE_OVERLAY_ID = 'liteTvTuneOverlay';
    var NEXT_OVERLAY_ID = 'liteTvNextOverlay';
    var NEXT_OVERLAY_WINDOW_SECONDS = 20;

    // Tuned state for this browser tab. mode: 'schedule' | 'binge' | 'offschedule'
    var tuned = null;
    var watchTimer = null;
    var chainInProgress = false;

    function apiGet(path) {
        return window.ApiClient.fetch({ url: window.ApiClient.getUrl(path), type: 'GET', dataType: 'json' });
    }

    function apiPost(path) {
        return window.ApiClient.fetch({ url: window.ApiClient.getUrl(path), type: 'POST' });
    }

    function apiDelete(path) {
        return window.ApiClient.fetch({ url: window.ApiClient.getUrl(path), type: 'DELETE' });
    }

    function getOwnSession() {
        var apiClient = window.ApiClient;
        return apiClient.getSessions({ deviceId: apiClient.deviceId() }).then(function (sessions) {
            return (sessions || [])[0] || null;
        });
    }

    function formatTime(isoUtc) {
        try {
            return new Date(isoUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        } catch (e) {
            return '';
        }
    }

    function ensureStyle() {
        if (document.getElementById(STYLE_ID)) {
            return;
        }
        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent =
            '#' + HOME_ROW_ID + ' { padding: 0 3.3%; margin-bottom: 1em; }' +
            '#' + HOME_ROW_ID + ' h2 { font-size: 1.2em; margin: 0.5em 0; }' +
            '#' + HOME_ROW_ID + ' .liteTvCards { display: flex; gap: 0.8em; overflow-x: auto; padding-bottom: 0.4em; }' +
            '.liteTvCard {' +
            '  min-width: 16em; max-width: 16em; border-radius: 0.4em; cursor: pointer;' +
            '  background: rgba(128, 128, 128, 0.16); padding: 0.9em 1em; box-sizing: border-box;' +
            '}' +
            '.liteTvCard:hover { background: rgba(128, 128, 128, 0.3); }' +
            '.liteTvCard .liteTvChannelName { font-weight: 700; margin-bottom: 0.35em; }' +
            '.liteTvCard .liteTvNow { font-size: 0.95em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }' +
            '.liteTvCard .liteTvNext { font-size: 0.85em; opacity: 0.75; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }' +
            '.liteTvProgress { height: 0.2em; border-radius: 0.1em; background: rgba(128,128,128,0.35); margin: 0.5em 0 0.35em; }' +
            '.liteTvProgress > div { height: 100%; border-radius: 0.1em; background: #00a4dc; }' +
            '#liteTvHeaderBtn { margin: 0 0.2em; }' +
            '#' + GUIDE_ID + ' {' +
            '  position: fixed; inset: 0; z-index: 1200; background: rgba(0, 0, 0, 0.72);' +
            '  display: flex; align-items: center; justify-content: center;' +
            '}' +
            '#' + GUIDE_ID + ' .liteTvPanel {' +
            '  background: #202020; color: #fff; border-radius: 0.5em; width: min(34em, 92vw);' +
            '  max-height: 82vh; overflow-y: auto; padding: 1.2em 1.4em; box-shadow: 0 0.4em 2em rgba(0,0,0,0.7);' +
            '  font-size: clamp(12px, 1.05vw, 16px);' +
            '}' +
            '#' + GUIDE_ID + ' h2 { margin: 0 0 0.8em; font-size: 1.3em; }' +
            '#' + GUIDE_ID + ' .liteTvGuideChannel { border-top: 1px solid rgba(255,255,255,0.12); padding: 0.8em 0; }' +
            '#' + GUIDE_ID + ' .liteTvGuideHead { display: flex; align-items: center; justify-content: space-between; gap: 0.8em; }' +
            '#' + GUIDE_ID + ' .liteTvGuideName { font-weight: 700; font-size: 1.1em; }' +
            '#' + GUIDE_ID + ' .liteTvGuideActions { display: flex; gap: 0.5em; flex-shrink: 0; }' +
            '#' + GUIDE_ID + ' button {' +
            '  background: #00a4dc; color: #fff; border: 0; border-radius: 0.3em;' +
            '  padding: 0.45em 0.9em; cursor: pointer; font-size: 0.95em;' +
            '}' +
            '#' + GUIDE_ID + ' button.liteTvSecondary { background: rgba(255,255,255,0.14); }' +
            '#' + GUIDE_ID + ' .liteTvEpg { margin: 0.4em 0 0; font-size: 0.92em; opacity: 0.9; }' +
            '#' + GUIDE_ID + ' .liteTvEpg div { margin: 0.15em 0; }' +
            '#' + GUIDE_ID + ' .liteTvDevices { margin-top: 0.5em; display: none; }' +
            '#' + GUIDE_ID + ' .liteTvDevices button { display: block; width: 100%; text-align: left; margin: 0.3em 0; background: rgba(255,255,255,0.14); }' +
            '#' + TUNE_OVERLAY_ID + ', #' + NEXT_OVERLAY_ID + ' {' +
            '  position: absolute; z-index: 1000; pointer-events: none;' +
            '  opacity: 0; transition: opacity 0.5s ease;' +
            '  font-size: clamp(11px, 1.1vw, 17px); color: #fff;' +
            '}' +
            '#' + TUNE_OVERLAY_ID + '.liteTvVisible, #' + NEXT_OVERLAY_ID + '.liteTvVisible { opacity: 1; }' +
            '#' + TUNE_OVERLAY_ID + ' { top: 8%; right: 4%; text-align: right; }' +
            '#' + TUNE_OVERLAY_ID + ' .liteTvBug {' +
            '  display: inline-block; background: rgba(0, 0, 0, 0.55); border-radius: 0.3em;' +
            '  padding: 0.5em 0.9em; font-weight: 700; text-shadow: 0 0.05em 0.3em rgba(0,0,0,0.8);' +
            '}' +
            '#' + TUNE_OVERLAY_ID + ' button, #' + NEXT_OVERLAY_ID + ' button {' +
            '  pointer-events: auto; background: rgba(255, 255, 255, 0.92); color: #000; border: 0;' +
            '  border-radius: 0.3em; padding: 0.5em 1em; margin-top: 0.5em; cursor: pointer;' +
            '  font-size: 1em; font-weight: 600; display: block; margin-left: auto;' +
            '}' +
            '#' + NEXT_OVERLAY_ID + ' { bottom: 18%; right: 4%; text-align: right; }' +
            '#' + NEXT_OVERLAY_ID + ' .liteTvNextTitle {' +
            '  background: rgba(0, 0, 0, 0.55); border-radius: 0.3em; padding: 0.6em 1em;' +
            '  display: inline-block; text-shadow: 0 0.05em 0.3em rgba(0,0,0,0.8);' +
            '}' +
            '#' + NEXT_OVERLAY_ID + ' .liteTvNextTitle b { display: block; font-size: 1.15em; }' +
            '#' + NEXT_OVERLAY_ID + ' .liteTvButtons { display: flex; gap: 0.5em; justify-content: flex-end; }' +
            '#' + NEXT_OVERLAY_ID + ' button.liteTvActive { outline: 0.15em solid #00a4dc; }';
        document.head.appendChild(style);
    }

    // ---------------------------------------------------------------- playback

    function playItem(itemId, positionTicks) {
        return getOwnSession().then(function (session) {
            if (!session) {
                throw new Error('own session not found');
            }
            var url = window.ApiClient.getUrl('Sessions/' + session.Id + '/Playing', {
                playCommand: 'PlayNow',
                itemIds: itemId,
                startPositionTicks: Math.max(0, Math.round(positionTicks || 0))
            });
            return window.ApiClient.fetch({ url: url, type: 'POST' }).then(function () {
                return session.Id;
            });
        });
    }

    function tuneIn(channelId) {
        closeGuide();
        return apiGet('LiteTv/Channels/' + channelId + '/Now?upcoming=1').then(function (now) {
            var fetchedAt = Date.now();
            var offsetTicks = now.OffsetTicks + (Date.now() - fetchedAt) * 10000;
            return playItem(now.Current.ItemId, offsetTicks).then(function (sessionId) {
                tuned = {
                    channelId: channelId,
                    channelName: now.ChannelName,
                    sessionId: sessionId,
                    mode: 'schedule',
                    currentItemId: now.Current.ItemId,
                    currentSeriesId: now.Current.SeriesId || null,
                    startedFresh: false
                };
                chainInProgress = true; // survives the osd transition
                apiPost('LiteTv/Tuned?sessionId=' + encodeURIComponent(sessionId) + '&channelId=' + channelId)
                    .catch(function () { /* hygiene is best-effort */ });
                showTuneOverlay(now);
            });
        }).catch(function (err) {
            console.warn('liteTv: tune-in failed', err);
        });
    }

    function untune() {
        if (!tuned) {
            return;
        }
        var sessionId = tuned.sessionId;
        tuned = null;
        chainInProgress = false;
        stopWatcher();
        removeOverlay(TUNE_OVERLAY_ID);
        removeOverlay(NEXT_OVERLAY_ID);
        if (sessionId) {
            apiDelete('LiteTv/Tuned?sessionId=' + encodeURIComponent(sessionId)).catch(function () { });
        }
    }

    // ---------------------------------------------------------------- overlays

    function getOsdContainer() {
        return document.querySelector('#videoOsdPage:not(.hide)') || document.querySelector('#videoOsdPage') || document.body;
    }

    function removeOverlay(id) {
        var el = document.getElementById(id);
        if (el && el.parentNode) {
            el.parentNode.removeChild(el);
        }
    }

    function showTuneOverlay(now) {
        ensureStyle();
        removeOverlay(TUNE_OVERLAY_ID);

        var overlay = document.createElement('div');
        overlay.id = TUNE_OVERLAY_ID;

        var bug = document.createElement('div');
        bug.className = 'liteTvBug';
        bug.textContent = '📺 ' + now.ChannelName;
        overlay.appendChild(bug);

        var restart = document.createElement('button');
        restart.textContent = '▶ Von Anfang an';
        restart.addEventListener('click', function () {
            removeOverlay(TUNE_OVERLAY_ID);
            if (!tuned) {
                return;
            }
            tuned.mode = 'offschedule';
            tuned.startedFresh = true;
            chainInProgress = true;
            playItem(tuned.currentItemId, 0).catch(function (err) {
                console.warn('liteTv: restart failed', err);
            });
        });
        overlay.appendChild(restart);

        var container = getOsdContainer();
        container.appendChild(overlay);
        void overlay.offsetWidth;
        overlay.classList.add('liteTvVisible');

        setTimeout(function () {
            overlay.classList.remove('liteTvVisible');
            setTimeout(function () { removeOverlay(TUNE_OVERLAY_ID); }, 600);
        }, 8000);
    }

    // ------------------------------------------------------- end-of-item logic

    function stopWatcher() {
        if (watchTimer) {
            clearInterval(watchTimer);
            watchTimer = null;
        }
    }

    function startWatcher() {
        stopWatcher();
        var overlayShown = false;
        var fired = false;
        var nextInfo = null; // { schedule: ProgramDto|null, binge: {Id, Name}|null }

        watchTimer = setInterval(function () {
            if (!tuned) {
                stopWatcher();
                return;
            }
            var video = document.querySelector('#videoOsdPage video') || document.querySelector('video');
            if (!video || !video.duration || isNaN(video.duration)) {
                return;
            }
            var remaining = video.duration - video.currentTime;

            if (remaining <= NEXT_OVERLAY_WINDOW_SECONDS && !overlayShown) {
                overlayShown = true;
                prepareNext().then(function (info) {
                    nextInfo = info;
                    if (tuned && info) {
                        showNextOverlay(info, Math.max(1, Math.floor(remaining)));
                    }
                });
            }

            if ((remaining <= 0.5 || video.ended) && !fired) {
                fired = true;
                stopWatcher();
                playNext(nextInfo);
            }
        }, 500);
    }

    function prepareNext() {
        if (!tuned) {
            return Promise.resolve(null);
        }
        var schedulePromise = apiGet('LiteTv/Channels/' + tuned.channelId + '/Now?upcoming=1').then(function (now) {
            // Near the end of the current item, upcoming[0] is the follow-up program.
            return (now.Upcoming && now.Upcoming[0]) || null;
        }).catch(function () { return null; });

        var bingePromise = Promise.resolve(null);
        if (tuned.currentSeriesId) {
            bingePromise = window.ApiClient.getEpisodes(tuned.currentSeriesId, {
                userId: window.ApiClient.getCurrentUserId(),
                fields: 'Id,Name'
            }).then(function (result) {
                var episodes = (result && result.Items) || [];
                for (var i = 0; i < episodes.length; i++) {
                    if (episodes[i].Id === tuned.currentItemId && i + 1 < episodes.length) {
                        return episodes[i + 1];
                    }
                }
                return null;
            }).catch(function () { return null; });
        }

        return Promise.all([schedulePromise, bingePromise]).then(function (results) {
            return { schedule: results[0], binge: results[1] };
        });
    }

    function showNextOverlay(info, countdownSeconds) {
        ensureStyle();
        removeOverlay(NEXT_OVERLAY_ID);

        var overlay = document.createElement('div');
        overlay.id = NEXT_OVERLAY_ID;

        var title = document.createElement('div');
        title.className = 'liteTvNextTitle';
        overlay.appendChild(title);

        var buttons = document.createElement('div');
        buttons.className = 'liteTvButtons';
        overlay.appendChild(buttons);

        var scheduleBtn = document.createElement('button');
        var bingeBtn = null;

        function scheduleName() {
            if (!info.schedule) {
                return 'Programm';
            }
            return info.schedule.SeriesName
                ? info.schedule.SeriesName + ': ' + info.schedule.Name
                : info.schedule.Name;
        }

        function updateTitle(secondsLeft) {
            var name = tuned && tuned.mode === 'binge' && info.binge ? info.binge.Name : scheduleName();
            title.innerHTML = '';
            var b = document.createElement('b');
            b.textContent = 'Als Nächstes: ' + name;
            var small = document.createElement('span');
            small.textContent = secondsLeft > 0 ? 'startet in ' + secondsLeft + ' s' : 'startet gleich';
            title.appendChild(b);
            title.appendChild(small);
        }

        function refreshActive() {
            if (!tuned) {
                return;
            }
            scheduleBtn.classList.toggle('liteTvActive', tuned.mode !== 'binge');
            if (bingeBtn) {
                bingeBtn.classList.toggle('liteTvActive', tuned.mode === 'binge');
            }
        }

        if (info.binge && tuned && tuned.currentSeriesId) {
            bingeBtn = document.createElement('button');
            bingeBtn.textContent = 'Serie weiterschauen';
            bingeBtn.addEventListener('click', function () {
                if (tuned) {
                    tuned.mode = 'binge';
                }
                refreshActive();
                updateTitle(secondsLeft);
            });
            buttons.appendChild(bingeBtn);
        }

        scheduleBtn.textContent = 'Programm folgen';
        scheduleBtn.addEventListener('click', function () {
            if (tuned) {
                tuned.mode = 'schedule';
            }
            refreshActive();
            updateTitle(secondsLeft);
        });
        buttons.appendChild(scheduleBtn);

        var secondsLeft = countdownSeconds;
        updateTitle(secondsLeft);
        refreshActive();

        var countdown = setInterval(function () {
            secondsLeft--;
            if (secondsLeft < 0 || !document.getElementById(NEXT_OVERLAY_ID)) {
                clearInterval(countdown);
                return;
            }
            updateTitle(secondsLeft);
        }, 1000);

        getOsdContainer().appendChild(overlay);
        void overlay.offsetWidth;
        overlay.classList.add('liteTvVisible');
    }

    function playNext(info) {
        removeOverlay(NEXT_OVERLAY_ID);
        if (!tuned) {
            return;
        }

        chainInProgress = true;
        if (tuned.mode === 'binge' && info && info.binge) {
            tuned.currentItemId = info.binge.Id;
            playItem(info.binge.Id, 0).catch(function (err) {
                console.warn('liteTv: binge next failed', err);
            });
            return;
        }

        // Follow the schedule: re-resolve so we stay clock-accurate even after
        // off-schedule detours (restart from beginning, binge episodes).
        tuned.mode = 'schedule';
        apiGet('LiteTv/Channels/' + tuned.channelId + '/Now?upcoming=1').then(function (now) {
            tuned.currentItemId = now.Current.ItemId;
            tuned.currentSeriesId = now.Current.SeriesId || null;
            return playItem(now.Current.ItemId, now.OffsetTicks);
        }).catch(function (err) {
            console.warn('liteTv: schedule next failed', err);
            untune();
        });
    }

    // ------------------------------------------------------------------ guide

    function buildChannelCard(channel) {
        var card = document.createElement('div');
        card.className = 'liteTvCard';

        var name = document.createElement('div');
        name.className = 'liteTvChannelName';
        name.textContent = '📺 ' + channel.Name;
        card.appendChild(name);

        var now = document.createElement('div');
        now.className = 'liteTvNow';
        if (channel.Now) {
            now.textContent = (channel.Now.SeriesName ? channel.Now.SeriesName + ': ' : '') + channel.Now.Name;
        } else {
            now.textContent = 'Sendepause';
        }
        card.appendChild(now);

        if (channel.Now) {
            var start = new Date(channel.Now.StartUtc).getTime();
            var end = new Date(channel.Now.EndUtc).getTime();
            var pct = end > start ? Math.min(100, Math.max(0, ((Date.now() - start) / (end - start)) * 100)) : 0;
            var progress = document.createElement('div');
            progress.className = 'liteTvProgress';
            var bar = document.createElement('div');
            bar.style.width = pct.toFixed(1) + '%';
            progress.appendChild(bar);
            card.appendChild(progress);
        }

        var next = document.createElement('div');
        next.className = 'liteTvNext';
        if (channel.Next) {
            next.textContent = 'Danach ' + formatTime(channel.Next.StartUtc) + ': '
                + (channel.Next.SeriesName ? channel.Next.SeriesName + ': ' : '') + channel.Next.Name;
        }
        card.appendChild(next);

        card.addEventListener('click', function () {
            tuneIn(channel.Id);
        });
        return card;
    }

    function renderHomeRow(page) {
        apiGet('LiteTv/Channels').then(function (guide) {
            if (!guide.EnableWebUi || !guide.ShowHomeRow || !guide.Channels.length) {
                return;
            }
            ensureStyle();
            var container = page.querySelector('.homeSectionsContainer') || page;
            var existing = document.getElementById(HOME_ROW_ID);
            if (existing && existing.parentNode) {
                existing.parentNode.removeChild(existing);
            }

            var section = document.createElement('div');
            section.id = HOME_ROW_ID;
            section.className = 'verticalSection';
            var heading = document.createElement('h2');
            heading.className = 'sectionTitle';
            heading.textContent = 'TV-Sender';
            section.appendChild(heading);

            var cards = document.createElement('div');
            cards.className = 'liteTvCards';
            guide.Channels.forEach(function (channel) {
                cards.appendChild(buildChannelCard(channel));
            });
            section.appendChild(cards);
            container.appendChild(section);
        }).catch(function (err) {
            console.debug('liteTv: guide not available', err);
        });
    }

    function closeGuide() {
        removeOverlay(GUIDE_ID);
    }

    function openGuide() {
        closeGuide();
        ensureStyle();
        apiGet('LiteTv/Channels').then(function (guide) {
            var backdrop = document.createElement('div');
            backdrop.id = GUIDE_ID;
            backdrop.addEventListener('click', function (e) {
                if (e.target === backdrop) {
                    closeGuide();
                }
            });

            var panel = document.createElement('div');
            panel.className = 'liteTvPanel';
            backdrop.appendChild(panel);

            var heading = document.createElement('h2');
            heading.textContent = '📺 TV-Sender';
            panel.appendChild(heading);

            if (!guide.Channels.length) {
                var empty = document.createElement('div');
                empty.textContent = 'Keine Sender konfiguriert. Sender werden im Dashboard unter Plugins → LiteTV Channels angelegt.';
                panel.appendChild(empty);
            }

            guide.Channels.forEach(function (channel) {
                var row = document.createElement('div');
                row.className = 'liteTvGuideChannel';

                var head = document.createElement('div');
                head.className = 'liteTvGuideHead';
                var name = document.createElement('div');
                name.className = 'liteTvGuideName';
                name.textContent = channel.Name;
                head.appendChild(name);

                var actions = document.createElement('div');
                actions.className = 'liteTvGuideActions';
                var playBtn = document.createElement('button');
                playBtn.textContent = '▶ Ansehen';
                playBtn.addEventListener('click', function () {
                    tuneIn(channel.Id);
                });
                actions.appendChild(playBtn);

                var castBtn = document.createElement('button');
                castBtn.className = 'liteTvSecondary';
                castBtn.textContent = 'Auf Gerät…';
                actions.appendChild(castBtn);
                head.appendChild(actions);
                row.appendChild(head);

                var epg = document.createElement('div');
                epg.className = 'liteTvEpg';
                if (channel.Now) {
                    var nowLine = document.createElement('div');
                    nowLine.textContent = 'Jetzt: ' + (channel.Now.SeriesName ? channel.Now.SeriesName + ': ' : '') + channel.Now.Name
                        + ' (bis ' + formatTime(channel.Now.EndUtc) + ')';
                    epg.appendChild(nowLine);
                }
                if (channel.Next) {
                    var nextLine = document.createElement('div');
                    nextLine.textContent = 'Danach ' + formatTime(channel.Next.StartUtc) + ': '
                        + (channel.Next.SeriesName ? channel.Next.SeriesName + ': ' : '') + channel.Next.Name;
                    epg.appendChild(nextLine);
                }
                row.appendChild(epg);

                var devices = document.createElement('div');
                devices.className = 'liteTvDevices';
                row.appendChild(devices);

                castBtn.addEventListener('click', function () {
                    if (devices.style.display === 'block') {
                        devices.style.display = 'none';
                        return;
                    }
                    devices.style.display = 'block';
                    devices.innerHTML = '';
                    window.ApiClient.getSessions().then(function (sessions) {
                        var ownDeviceId = window.ApiClient.deviceId();
                        var targets = (sessions || []).filter(function (s) {
                            return s.DeviceId !== ownDeviceId
                                && s.SupportsRemoteControl !== false;
                        });
                        if (!targets.length) {
                            var none = document.createElement('div');
                            none.textContent = 'Keine anderen aktiven Geräte gefunden.';
                            devices.appendChild(none);
                            return;
                        }
                        targets.forEach(function (s) {
                            var btn = document.createElement('button');
                            btn.textContent = (s.DeviceName || s.Client || 'Gerät') + (s.UserName ? ' – ' + s.UserName : '');
                            btn.addEventListener('click', function () {
                                apiPost('LiteTv/Channels/' + channel.Id + '/PlayOn/' + encodeURIComponent(s.Id)).then(function () {
                                    closeGuide();
                                }).catch(function (err) {
                                    console.warn('liteTv: play on device failed', err);
                                });
                            });
                            devices.appendChild(btn);
                        });
                    });
                });

                panel.appendChild(row);
            });

            document.body.appendChild(backdrop);
        }).catch(function (err) {
            console.debug('liteTv: guide not available', err);
        });
    }

    function ensureHeaderButton() {
        if (document.getElementById('liteTvHeaderBtn')) {
            return;
        }
        var headerRight = document.querySelector('.headerRight');
        if (!headerRight) {
            return;
        }
        apiGet('LiteTv/Channels').then(function (guide) {
            if (!guide.EnableWebUi || document.getElementById('liteTvHeaderBtn')) {
                return;
            }
            var btn = document.createElement('button');
            btn.id = 'liteTvHeaderBtn';
            btn.type = 'button';
            btn.className = 'headerButton paper-icon-button-light';
            btn.title = 'TV-Sender';
            btn.textContent = '📺';
            btn.addEventListener('click', openGuide);
            headerRight.insertBefore(btn, headerRight.firstChild);
        }).catch(function () { });
    }

    // ------------------------------------------------------------- page hooks

    function isVideoOsd(e) {
        if (e && e.detail && typeof e.detail.type === 'string') {
            return e.detail.type === 'video-osd';
        }
        var page = e && e.target;
        return !!(page && page.id === 'videoOsdPage') || !!document.querySelector('#videoOsdPage:not(.hide)');
    }

    function isHome(e) {
        if (e && e.detail && typeof e.detail.type === 'string') {
            return e.detail.type === 'home';
        }
        var page = e && e.target;
        return !!(page && page.id === 'indexPage');
    }

    document.addEventListener('viewshow', function (e) {
        if (!window.ApiClient) {
            return;
        }
        ensureHeaderButton();

        if (isHome(e) && e.target) {
            renderHomeRow(e.target);
            return;
        }

        if (isVideoOsd(e)) {
            if (tuned) {
                chainInProgress = false;
                startWatcher();
            }
            return;
        }

        // Any other page: when we are not mid-chain (the osd briefly hides while
        // switching items), the viewer has left the channel.
        if (tuned && !chainInProgress) {
            untune();
        }
    });

    document.addEventListener('viewhide', function (e) {
        if (e && e.target && e.target.id === 'videoOsdPage') {
            stopWatcher();
            removeOverlay(TUNE_OVERLAY_ID);
            removeOverlay(NEXT_OVERLAY_ID);
        }
    });
})();
