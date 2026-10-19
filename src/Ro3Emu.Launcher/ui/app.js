// Hero Saga Launcher Controller Script
// Manages internationalization (PT-BR, EN, ES), state, UI rendering, news, and WebView2 communications.

(function () {
  "use strict";

  var $ = function (id) { return document.getElementById(id); };
  var host;
  var state = { phase: "checking" };
  var busy = false;
  var stage = null;
  var serverData = { online: null, players: null };
  var remoteNews = null;

  var I18N = {
    "pt-br": {
      langCode: "PT",
      tabs: {
        home: "INÍCIO",
        news: "NOTÍCIAS",
        register: "CRIAR CONTA",
        discord: "DISCORD",
        settings: "CONFIGURAÇÕES"
      },
      server: {
        online: "Online",
        offline: "Offline",
        players: "jogadores",
        player: "jogador",
        connecting: "Conectando…"
      },
      notices: {
        title: "AVISOS",
        badge: "CONTEÚDO ASSINADO",
        sub: "Anúncios oficiais da equipe Hero Saga.",
        foot: "CANAL OFICIAL DE NOTÍCIAS · PT-BR"
      },
      defaultNews: [
        {
          tag: "WELCOME",
          color: "blue",
          title: "Prepare-se para o lançamento",
          text: "Os portões de Midgard se abrem em breve. Junte-se ao nosso Discord oficial para acompanhar tudo!",
          date: "2026-10-19"
        },
        {
          tag: "STATUS",
          color: "red",
          title: "Servidores em preparação",
          text: "Estamos ajustando os detalhes finais para o lançamento. Verifique o Discord para avisos.",
          date: "2026-10-18"
        },
        {
          tag: "UPDATES",
          color: "orange",
          title: "Atualizações automáticas",
          text: "O inicializador baixa e atualiza todos os arquivos necessários de forma rápida e segura.",
          date: "2026-10-15"
        }
      ],
      steps: {
        s1Title: "INSTALAR",
        s1Desc: "Baixar arquivos do jogo",
        s2Title: "VERIFICAR",
        s2Desc: "Checar integridade dos arquivos",
        s3Title: "PRONTO",
        s3Desc: "Iniciar ou reparar"
      },
      actions: {
        normal: "AÇÕES DO JOGO",
        recovery: "AÇÕES DE RECUPERAÇÃO",
        repair: "Reparar"
      },
      phases: {
        checking: { button: "Verificando…", text: "Verificando atualizações…" },
        install: { button: "Instalar", text: "O jogo ainda não está instalado. Baixar arquivos necessários." },
        update: { button: "Atualizar", text: "Uma nova versão está disponível para download." },
        repair: { button: "Reparar", text: "Alguns arquivos diferem da versão oficial." },
        ready: { button: "Jogar", text: "Tudo atualizado. Bom jogo!" },
        offline: { button: "Tentar novamente", text: "Erro: não foi possível conectar ao servidor." },
        error: { button: "Tentar novamente", text: "Ocorreu um erro no inicializador." },
        launching: { button: "Iniciando…", text: "Iniciando o cliente do jogo…" },
        running: { button: "Jogar", text: "O jogo está em execução." },
        pause: { button: "Pausar" }
      },
      stages: {
        scanning: "Verificando arquivos",
        downloading: "Baixando",
        assembling: "Montando arquivos",
        verifying: "Verificando",
        committing: "Salvando"
      },
      progress: {
        left: "restante"
      },
      settings: {
        title: "Configurações",
        sub: "Onde o jogo está instalado neste computador.",
        folder: "Pasta do jogo",
        btnChange: "Alterar pasta",
        btnOpen: "Abrir pasta",
        hint: "Já possui o cliente instalado? Aponte para a pasta e apenas os arquivos atualizados serão baixados."
      }
    },
    "en": {
      langCode: "EN",
      tabs: {
        home: "HOME",
        news: "NEWS",
        register: "REGISTER",
        discord: "DISCORD",
        settings: "SETTINGS"
      },
      server: {
        online: "Online",
        offline: "Offline",
        players: "players",
        player: "player",
        connecting: "Connecting…"
      },
      notices: {
        title: "NOTICES",
        badge: "VERIFIED CONTENT",
        sub: "Official announcements from the Hero Saga team.",
        foot: "OFFICIAL NEWS CHANNEL · EN"
      },
      defaultNews: [
        {
          tag: "WELCOME",
          color: "blue",
          title: "Get ready for launch",
          text: "The gates of Midgard will open soon. Join our official Discord to follow all the latest news!",
          date: "2026-10-19"
        },
        {
          tag: "STATUS",
          color: "red",
          title: "Servers under preparation",
          text: "We are putting the final touches on Hero Saga for release. Check Discord for announcements.",
          date: "2026-10-18"
        },
        {
          tag: "UPDATES",
          color: "orange",
          title: "Automatic updates",
          text: "The launcher downloads and keeps all necessary game files up to date automatically.",
          date: "2026-10-15"
        }
      ],
      steps: {
        s1Title: "INSTALL",
        s1Desc: "Download game files",
        s2Title: "VERIFY",
        s2Desc: "Check file integrity",
        s3Title: "READY",
        s3Desc: "Launch or repair"
      },
      actions: {
        normal: "GAME ACTIONS",
        recovery: "RECOVERY ACTIONS",
        repair: "Repair"
      },
      phases: {
        checking: { button: "Checking…", text: "Checking for updates…" },
        install: { button: "Install", text: "The game is not installed yet. Download required files." },
        update: { button: "Update", text: "A new version is available for download." },
        repair: { button: "Repair", text: "Some files differ from the official release." },
        ready: { button: "Play", text: "Up to date. Have fun!" },
        offline: { button: "Try again", text: "Error: Could not connect to update server." },
        error: { button: "Try again", text: "Something went wrong in the launcher." },
        launching: { button: "Starting…", text: "Starting the game client…" },
        running: { button: "Play", text: "The game is currently running." },
        pause: { button: "Pause" }
      },
      stages: {
        scanning: "Checking files",
        downloading: "Downloading",
        assembling: "Assembling files",
        verifying: "Verifying",
        committing: "Saving"
      },
      progress: {
        left: "left"
      },
      settings: {
        title: "Settings",
        sub: "Where the game lives on this PC.",
        folder: "Game folder",
        btnChange: "Change folder",
        btnOpen: "Open folder",
        hint: "Already have the RO3 PC client? Point this at its folder and only changed files are downloaded."
      }
    },
    "es": {
      langCode: "ES",
      tabs: {
        home: "INICIO",
        news: "NOTICIAS",
        register: "CREAR CUENTA",
        discord: "DISCORD",
        settings: "CONFIGURACIÓN"
      },
      server: {
        online: "Online",
        offline: "Offline",
        players: "jugadores",
        player: "jugador",
        connecting: "Conectando…"
      },
      notices: {
        title: "AVISOS",
        badge: "CONTENIDO FIRMADO",
        sub: "Anuncios oficiales del equipo Hero Saga.",
        foot: "CANAL OFICIAL DE NOTICIAS · ES"
      },
      defaultNews: [
        {
          tag: "WELCOME",
          color: "blue",
          title: "Prepárate para el lanzamiento",
          text: "Las puertas de Midgard se abrirán pronto. ¡Únete a nuestro Discord oficial para seguir todas las novedades!",
          date: "2026-10-19"
        },
        {
          tag: "STATUS",
          color: "red",
          title: "Servidores en preparación",
          text: "Estamos afinando los últimos detalles para el lanzamiento. Consulta Discord para avisos.",
          date: "2026-10-18"
        },
        {
          tag: "UPDATES",
          color: "orange",
          title: "Actualizaciones automáticas",
          text: "El lanzador descarga y actualiza todos los archivos necesarios de forma rápida y segura.",
          date: "2026-10-15"
        }
      ],
      steps: {
        s1Title: "INSTALAR",
        s1Desc: "Descargar archivos del juego",
        s2Title: "VERIFICAR",
        s2Desc: "Checar integridad de los archivos",
        s3Title: "LISTO",
        s3Desc: "Iniciar o reparar"
      },
      actions: {
        normal: "ACCIONES DEL JUEGO",
        recovery: "ACCIONES DE RECUPERACIÓN",
        repair: "Reparar"
      },
      phases: {
        checking: { button: "Verificando…", text: "Buscando actualizaciones…" },
        install: { button: "Instalar", text: "El juego aún no está instalado. Descargar archivos." },
        update: { button: "Actualizar", text: "Una nueva versión está disponible para descargar." },
        repair: { button: "Reparar", text: "Algunos archivos difieren de la versión oficial." },
        ready: { button: "Jugar", text: "¡Todo actualizado! ¡Que disfrutes del juego!" },
        offline: { button: "Reintentar", text: "Error: No se pudo conectar al servidor." },
        error: { button: "Reintentar", text: "Ocurrió un error en el actualizador." },
        launching: { button: "Iniciando…", text: "Iniciando el cliente del juego…" },
        running: { button: "Jugar", text: "El juego está en ejecución." },
        pause: { button: "Pausar" }
      },
      stages: {
        scanning: "Comprobando archivos",
        downloading: "Descargando",
        assembling: "Ensamblando archivos",
        verifying: "Verificando",
        committing: "Guardando"
      },
      progress: {
        left: "restante"
      },
      settings: {
        title: "Configuración",
        sub: "Dónde está instalado el juego en este PC.",
        folder: "Carpeta del juego",
        btnChange: "Cambiar carpeta",
        btnOpen: "Abrir carpeta",
        hint: "¿Ya tienes el cliente instalado? Apunta a la carpeta y solo se descargarán los archivos modificados."
      }
    }
  };

  var currentLang = localStorage.getItem("hero_lang") || "pt-br";
  if (!I18N[currentLang]) currentLang = "pt-br";

  var COLORS = { blue: "var(--blue)", green: "var(--green)", orange: "var(--orange)", red: "var(--red)" };

  function send(o) { host.post(o); }

  function size(b) {
    return b >= 1073741824 ? (b / 1073741824).toFixed(2) + " GB" : Math.round(b / 1048576) + " MB";
  }

  function eta(s) {
    if (!(s > 0) || !isFinite(s)) return "";
    s = Math.round(s);
    if (s >= 3600) return Math.floor(s / 3600) + " h " + Math.floor(s % 3600 / 60) + " min";
    if (s >= 60) return Math.floor(s / 60) + " min " + (s % 60) + " s";
    return s + " s";
  }

  // Icons SVG definitions
  var ICONS = {
    play: '<svg viewBox="0 0 24 24" class="icon-svg"><polygon points="8,5 19,12 8,19" fill="currentColor"/></svg>',
    download: '<svg viewBox="0 0 24 24" class="icon-svg"><path d="M12 4v11m-4-4l4 4 4-4M5 19h14" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" fill="none"/></svg>',
    wrench: '<svg viewBox="0 0 24 24" class="icon-svg"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6-4.6 4.6a2 2 0 0 1-1.3.6 2 2 0 0 1-1.4-.6l-.6-.6a2 2 0 0 1 0-2.8l4.6-4.6-1.6-1.6a1 1 0 0 0-1.4 0l-1.4 1.4a4 4 0 0 0 0 5.6l.6.6-4.3 4.3a1 1 0 0 0 0 1.4l2.8 2.8a1 1 0 0 0 1.4 0l4.3-4.3.6.6a4 4 0 0 0 5.6 0l1.4-1.4a1 1 0 0 0 0-1.4l-1.6-1.6 4.6-4.6a1 1 0 0 0-1.4-1.4l-4.6 4.6z" fill="currentColor"/></svg>',
    pause: '<svg viewBox="0 0 24 24" class="icon-svg"><rect x="7" y="5" width="3.2" height="14" fill="currentColor"/><rect x="13.8" y="5" width="3.2" height="14" fill="currentColor"/></svg>',
    spinner: '<svg viewBox="0 0 24 24" class="icon-svg"><polygon points="12,3 18,12 12,21 6,12" fill="currentColor"/></svg>'
  };

  var PHASES_CONFIG = {
    checking: { step: 0, icon: "spinner", wait: true },
    install: { step: 1, icon: "download" },
    update: { step: 1, icon: "download" },
    repair: { step: 2, icon: "wrench", warn: true, recovery: true },
    ready: { step: 3, icon: "play" },
    offline: { step: 0, icon: "wrench", warn: true, recovery: true },
    error: { step: 0, icon: "wrench", warn: true, recovery: true },
    launching: { step: 3, icon: "spinner", wait: true },
    running: { step: 3, icon: "play" }
  };

  function setLanguage(lang) {
    if (!I18N[lang]) return;
    currentLang = lang;
    localStorage.setItem("hero_lang", lang);

    var t = I18N[lang];
    $("current-lang").textContent = t.langCode;

    // Update active dropdown item
    document.querySelectorAll(".lang-opt").forEach(function (btn) {
      btn.classList.toggle("active", btn.dataset.lang === lang);
    });

    // Update tabs
    $("tab-home").textContent = t.tabs.home;
    $("tab-news").textContent = t.tabs.news;
    $("tab-register").textContent = t.tabs.register;
    $("tab-discord").textContent = t.tabs.discord;
    var tabSet = $("tab-settings");
    if (tabSet) tabSet.textContent = t.tabs.settings;

    // Update notices card
    $("notices-title").textContent = t.notices.title;
    $("notices-badge").textContent = t.notices.badge;
    $("notices-sub").textContent = t.notices.sub;
    $("notices-foot").textContent = t.notices.foot;

    // Update steps
    $("step-1-title").textContent = t.steps.s1Title;
    $("step-1-desc").textContent = t.steps.s1Desc;
    $("step-2-title").textContent = t.steps.s2Title;
    $("step-2-desc").textContent = t.steps.s2Desc;
    $("step-3-title").textContent = t.steps.s3Title;
    $("step-3-desc").textContent = t.steps.s3Desc;

    // Update settings card if present
    var sTitle = $("settings-title"); if (sTitle) sTitle.textContent = t.settings.title;
    var sSub = $("settings-sub"); if (sSub) sSub.textContent = t.settings.sub;
    var sLbl = $("settings-lbl-folder"); if (sLbl) sLbl.textContent = t.settings.folder;
    var sChg = $("set-dir-change"); if (sChg) sChg.textContent = t.settings.btnChange;
    var sOpn = $("set-open"); if (sOpn) sOpn.textContent = t.settings.btnOpen;
    var sHnt = $("settings-hint"); if (sHnt) sHnt.textContent = t.settings.hint;

    // Update server text with current data
    renderServerStatus();

    // Re-render news and main controls
    renderNews(remoteNews);
    render();
  }

  function setSteps(active) {
    for (var i = 1; i <= 3; i++) {
      var el = $("step-" + i);
      if (el) el.className = "step" + (i < active ? " done" : i === active ? " active" : "");
    }
  }

  function renderServerStatus() {
    var t = I18N[currentLang].server;
    if (serverData.online === null) {
      $("server").className = "server";
      $("server-text").innerHTML = "<b>" + t.connecting + "</b>";
      return;
    }
    $("server").className = "server " + (serverData.online ? "on" : "off");
    if (serverData.online) {
      var pText = "";
      if (serverData.players != null) {
        var unit = serverData.players === 1 ? t.player : t.players;
        pText = " · " + serverData.players + " " + unit;
      }
      $("server-text").innerHTML = "<b>" + t.online + "</b>" + pText;
    } else {
      $("server-text").innerHTML = "<b>" + t.offline + "</b>";
    }
  }

  var KNOWN_MESSAGES = {
    offline_installed: {
      "pt-br": "Não foi possível conectar ao servidor de atualização; jogando a versão instalada.",
      "en": "Can't reach the update server; playing the installed version.",
      "es": "No se pudo conectar al servidor de actualización; jugando la versión instalada."
    },
    offline_uninstalled: {
      "pt-br": "Não foi possível conectar ao servidor de atualização.",
      "en": "Can't reach the update server.",
      "es": "No se pudo conectar al servidor de actualización."
    },
    found_client: {
      "pt-br": "Cliente encontrado nesta pasta: apenas os arquivos modificados serão baixados.",
      "en": "Found an RO3 client in this folder: only the changed files will be downloaded.",
      "es": "Cliente encontrado en esta carpeta: solo se descargarán los archivos modificados."
    },
    paused: {
      "pt-br": "Pausado. Os arquivos baixados foram mantidos; continue a qualquer momento.",
      "en": "Paused. Downloaded parts are kept; continue any time.",
      "es": "Pausado. Los archivos descargados se conservan; continúa en cualquier momento."
    },
    ready: {
      "pt-br": "Tudo atualizado. Bom jogo!",
      "en": "Up to date. Have fun!",
      "es": "¡Todo actualizado! ¡Que disfrutes del juego!"
    },
    checking: {
      "pt-br": "Verificando atualizações…",
      "en": "Checking for updates…",
      "es": "Buscando actualizaciones…"
    },
    running: {
      "pt-br": "O jogo está em execução.",
      "en": "The game is running.",
      "es": "El juego se está ejecutando."
    },
    starting: {
      "pt-br": "Iniciando o cliente do jogo…",
      "en": "Starting the game…",
      "es": "Iniciando el cliente del juego…"
    }
  };

  function translateMessage(rawMsg, phase) {
    if (!rawMsg) {
      var ph = I18N[currentLang].phases[phase];
      return ph ? ph.text : "";
    }

    var lang = currentLang;

    // Check offline installed message
    if (rawMsg.indexOf("Can't reach the update server; playing the installed version") !== -1 ||
        rawMsg.indexOf("Não foi possível conectar ao servidor de atualização; jogando a versão instalada") !== -1 ||
        rawMsg.indexOf("No se pudo conectar al servidor de actualización; jugando la versión instalada") !== -1) {
      return KNOWN_MESSAGES.offline_installed[lang];
    }

    // Check offline uninstalled
    if (rawMsg.indexOf("Can't reach the update server") !== -1 ||
        rawMsg.indexOf("Não foi possível conectar ao servidor de atualização") !== -1 ||
        rawMsg.indexOf("No se pudo conectar al servidor de actualización") !== -1) {
      return KNOWN_MESSAGES.offline_uninstalled[lang];
    }

    // Check found client
    if (rawMsg.indexOf("Found an RO3 client in this folder") !== -1 ||
        rawMsg.indexOf("Cliente encontrado nesta pasta") !== -1 ||
        rawMsg.indexOf("Cliente encontrado en esta carpeta") !== -1) {
      return KNOWN_MESSAGES.found_client[lang];
    }

    // Check paused
    if (rawMsg.indexOf("Paused. Downloaded parts are kept") !== -1 ||
        rawMsg.indexOf("Pausado. Os arquivos baixados foram mantidos") !== -1 ||
        rawMsg.indexOf("Pausado. Los arquivos descargados se conservan") !== -1) {
      return KNOWN_MESSAGES.paused[lang];
    }

    // Version X is available
    var mVer = rawMsg.match(/Version\s+([\w\.\-]+)\s+is available/i) ||
               rawMsg.match(/versão\s+([\w\.\-]+)\s+está disponível/i) ||
               rawMsg.match(/versión\s+([\w\.\-]+)\s+está disponible/i);
    if (mVer) {
      if (lang === "pt-br") return "A versão " + mVer[1] + " está disponível.";
      if (lang === "es") return "La versión " + mVer[1] + " está disponible.";
      return "Version " + mVer[1] + " is available.";
    }

    // X game files differ from the release
    var mDiff = rawMsg.match(/(\d+)\s+game files differ/i) ||
                rawMsg.match(/(\d+)\s+arquivos do jogo diferem/i) ||
                rawMsg.match(/(\d+)\s+archivos del juego difieren/i);
    if (mDiff) {
      if (lang === "pt-br") return mDiff[1] + " arquivos do jogo diferem da versão oficial.";
      if (lang === "es") return mDiff[1] + " archivos del juego difieren de la versión oficial.";
      return mDiff[1] + " game files differ from the release.";
    }

    // Updating the launcher to X
    var mUpLaunch = rawMsg.match(/Updating the launcher to\s+([\w\.\-]+)/i) ||
                    rawMsg.match(/Atualizando o inicializador para\s+([\w\.\-]+)/i);
    if (mUpLaunch) {
      if (lang === "pt-br") return "Atualizando o inicializador para " + mUpLaunch[1] + "…";
      if (lang === "es") return "Actualizando el lanzador a " + mUpLaunch[1] + "…";
      return "Updating the launcher to " + mUpLaunch[1] + "…";
    }

    // The game did not start: X
    var mFail = rawMsg.match(/The game did not start:\s*(.+)/i) ||
                rawMsg.match(/O jogo não iniciou:\s*(.+)/i);
    if (mFail) {
      if (lang === "pt-br") return "O jogo não iniciou: " + mFail[1];
      if (lang === "es") return "El juego no se inició: " + mFail[1];
      return "The game did not start: " + mFail[1];
    }

    // Ready message
    if (rawMsg.indexOf("Up to date") !== -1 ||
        rawMsg.indexOf("Tudo atualizado") !== -1 ||
        rawMsg.indexOf("Todo actualizado") !== -1) {
      return KNOWN_MESSAGES.ready[lang];
    }

    // Checking message
    if (rawMsg.indexOf("Checking for updates") !== -1 ||
        rawMsg.indexOf("Verificando atualizações") !== -1 ||
        rawMsg.indexOf("Buscando actualizaciones") !== -1) {
      return KNOWN_MESSAGES.checking[lang];
    }

    var phText = I18N[lang].phases[phase];
    return (phText && phText.text) ? phText.text : rawMsg;
  }

  function render() {
    var t = I18N[currentLang];
    var cfg = PHASES_CONFIG[state.phase] || PHASES_CONFIG.checking;
    var phaseTexts = t.phases[state.phase] || t.phases.checking;

    $("progress").hidden = !busy;
    $("message").hidden = busy;

    if (busy) {
      setSteps(stage === "verifying" || stage === "committing" ? 2 : 1);
    } else {
      setSteps(cfg.step);
    }

    var msg = $("message");
    msg.className = "message" + (cfg.warn ? " warn" : "");
    $("message-text").textContent = translateMessage(state.message, state.phase);
    $("msg-help").hidden = !cfg.warn;

    // Actions panel title
    $("actions-title").textContent = cfg.recovery ? t.actions.recovery : t.actions.normal;

    // Primary button
    var btn = $("primary");
    var icoWrap = $("primary-icon-wrap");
    if (busy) {
      btn.disabled = false;
      $("primary-label").textContent = t.phases.pause.button;
      icoWrap.innerHTML = ICONS.pause;
    } else {
      btn.disabled = !!cfg.wait;
      $("primary-label").textContent = phaseTexts.button;
      icoWrap.innerHTML = ICONS[cfg.icon] || ICONS.spinner;
    }

    // Repair button
    $("repair-label").textContent = t.actions.repair;
    $("repair").disabled = busy || !state.installed || !!cfg.wait;

    var setDirChange = $("set-dir-change");
    if (setDirChange) setDirChange.disabled = busy;

    // Footer Version: Displays Launcher version and Game version clearly
    var lVer = state.launcher || "1.0.2";
    var vStr = "Launcher v" + lVer.replace(/^v/, "");
    var cVer = state.installed || state.remote;
    if (cVer) {
      vStr += " · Jogo v" + cVer.replace(/^v/, "");
    }
    $("ver-line").textContent = vStr;
  }

  function renderProgress(m) {
    stage = m.stage;
    var t = I18N[currentLang];
    $("p-stage").textContent = t.stages[m.stage] || m.stage;
    var f = m.total > 0 ? Math.min(1, m.done / m.total) : 0;
    $("p-pct").textContent = Math.floor(f * 100) + "%";
    $("p-fill").style.width = (f * 100).toFixed(2) + "%";
    $("p-bytes").textContent = m.total > 0 ? size(m.done) + " / " + size(m.total) : "";
    $("p-rate").textContent = (m.stage === "downloading" && m.rate > 0)
      ? (m.rate / 1048576).toFixed(1) + " MB/s · " + eta(m.eta) + " " + t.progress.left
      : "";
  }

  function renderNews(items) {
    var list = $("news-list");
    list.innerHTML = "";
    var currentNews = (items && items.length) ? items : I18N[currentLang].defaultNews;
    currentNews.forEach(function (n) {
      var li = document.createElement("li");
      li.style.setProperty("--c", COLORS[n.color] || COLORS.blue);

      var topDiv = document.createElement("div");
      topDiv.className = "list-top";

      var tag = document.createElement("span");
      tag.className = "tag";
      tag.textContent = n.tag || "News";
      topDiv.appendChild(tag);

      if (n.date) {
        var dateSpan = document.createElement("span");
        dateSpan.className = "date";
        dateSpan.textContent = n.date;
        topDiv.appendChild(dateSpan);
      }

      var b = document.createElement("b");
      b.textContent = n.title;

      var p = document.createElement("p");
      p.textContent = n.text || "";

      li.appendChild(topDiv);
      li.appendChild(b);
      li.appendChild(p);
      list.appendChild(li);
    });
  }

  function showTab(name) {
    document.querySelectorAll(".tab").forEach(function (t) {
      t.classList.toggle("active", t.dataset.tab === name);
    });
    var settingsSec = $("settings");
    if (settingsSec) settingsSec.hidden = (name !== "settings");
    $("notices").hidden = (name === "settings");
    $("notices").classList.toggle("wide", name === "news");
  }

  function handle(m) {
    switch (m.type) {
      case "state":
        busy = (m.phase === "updating");
        var launcher = state.launcher;
        if (!busy) {
          state = m;
          state.launcher = launcher;
          stage = null;
        } else {
          state.installed = m.installed || state.installed;
          state.remote = m.remote || state.remote;
        }
        render();
        break;
      case "progress":
        if (!busy) busy = true;
        renderProgress(m);
        render();
        break;
      case "server":
        serverData.online = !!m.online;
        serverData.players = m.players != null ? m.players : null;
        renderServerStatus();
        break;
      case "settings":
        var dirEl = $("set-dir");
        if (dirEl) dirEl.textContent = m.dir;
        state.launcher = m.launcher;
        var setVer = $("set-version");
        if (setVer) setVer.textContent = "Launcher v" + m.launcher;
        render();
        break;
      case "news":
        remoteNews = m.items;
        renderNews(remoteNews);
        break;
    }
  }

  function bind() {
    $("drag").addEventListener("mousedown", function (e) {
      if (e.button === 0 && !e.target.closest("button") && !e.target.closest("a")) {
        send({ cmd: "window", action: "drag" });
      }
    });

    $("btn-min").onclick = function () { send({ cmd: "window", action: "minimize" }); };
    $("btn-close").onclick = function () { send({ cmd: "window", action: "close" }); };

    // Tabs
    document.querySelectorAll(".tab").forEach(function (t) {
      t.onclick = function () { showTab(t.dataset.tab); };
    });

    // Language dropdown toggle
    $("lang-btn").onclick = function (e) {
      e.stopPropagation();
      var drop = $("lang-dropdown");
      drop.hidden = !drop.hidden;
    };

    document.querySelectorAll(".lang-opt").forEach(function (opt) {
      opt.onclick = function (e) {
        e.stopPropagation();
        setLanguage(opt.dataset.lang);
        $("lang-dropdown").hidden = true;
      };
    });

    document.addEventListener("click", function () {
      var drop = $("lang-dropdown");
      if (drop) drop.hidden = true;
    });

    // Buttons
    $("primary").onclick = function () { send({ cmd: busy ? "pause" : "primary" }); };
    $("repair").onclick = function () { send({ cmd: "repair" }); };

    var setDirChange = $("set-dir-change");
    if (setDirChange) setDirChange.onclick = function () { send({ cmd: "changeDir" }); };

    var setOpen = $("set-open");
    if (setOpen) setOpen.onclick = function () { send({ cmd: "openDir" }); };
  }

  // Mock host for testing in a plain web browser
  function createMockHost(scenario) {
    function emit(o) { setTimeout(function () { handle(o); }, 0); }
    var remote = "2.2.13";
    return {
      post: function (c) {
        if (c.cmd !== "ready") return;
        emit({ type: "settings", dir: "C:\\Games\\HeroSaga\\Client", launcher: "1.0.1" });
        emit({ type: "server", online: scenario !== "offline", players: 124 });
        if (scenario === "offline") return emit({ type: "state", phase: "offline" });
        if (scenario === "offline_installed") {
          return emit({
            type: "state",
            phase: "ready",
            installed: "2.2.13",
            remote: remote,
            message: "Can't reach the update server; playing the installed version."
          });
        }
        if (scenario === "downloading") {
          emit({ type: "state", phase: "install", remote: remote });
          emit({ type: "progress", stage: "downloading", done: 4.2e9, total: 11.5e9, rate: 28.5 * 1048576, eta: 256 });
          return;
        }
        var phase = { install: "install", ready: "ready", repair: "repair" }[scenario] || "ready";
        emit({
          type: "state",
          phase: phase,
          installed: phase === "install" ? null : phase === "update" ? "2.2.10" : remote,
          remote: remote
        });
      }
    };
  }

  function start() {
    var params = new URLSearchParams(location.search);
    if (params.get("lang")) {
      var l = params.get("lang").toLowerCase();
      if (I18N[l]) currentLang = l;
    }
    var wv = window.chrome && window.chrome.webview;
    if (wv) {
      host = { post: function (o) { wv.postMessage(o); } };
      wv.addEventListener("message", function (e) { handle(e.data); });
      document.addEventListener("contextmenu", function (e) { e.preventDefault(); });
    } else {
      host = createMockHost(params.get("mock") || "ready");
    }
    bind();
    setLanguage(currentLang);
    send({ cmd: "ready" });
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
  else start();
})();
