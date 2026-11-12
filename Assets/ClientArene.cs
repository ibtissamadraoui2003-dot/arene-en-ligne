// =============================================================================
// ClientArene.cs : ARÈNE DE TIR MULTIJOUEUR, client Unity (C#)
// =============================================================================
// Se connecte au serveur Python (serveur_arene.py) en TCP. Messages JSON + '\n'.
//
// Ce que montre ce client (les techniques des vrais jeux en ligne) :
//  1. Le client n'envoie que ses ENTRÉES (touches, visée, tir), jamais sa position.
//     C'est le serveur qui simule le jeu et qui fait autorité.
//  2. PRÉDICTION côté client : on applique tout de suite nos propres entrées,
//     sans attendre le serveur, donc le jeu reste fluide même avec du lag.
//  3. RÉCONCILIATION : à chaque état reçu, on repart de la position donnée par
//     le serveur et on rejoue les entrées qu'il n'a pas encore confirmées.
//  4. INTERPOLATION des autres joueurs et EXTRAPOLATION des balles.
//
// Installation : mettre ce fichier ET le dossier Resources (images des personnages) dans Assets,
// lancer serveur_arene.py, puis Play. (Secours : images incluses dans la classe ImagesPersos en bas.)
// Au démarrage, un MENU permet de choisir son pseudo, son personnage et l'adresse IP du serveur.
// (Supprimer ClientReseau.cs s'il est encore dans Assets.)
//
// Touches : ZQSD / WASD / flèches = bouger, souris = viser, clic gauche ou Espace = tirer,
//           P = prédiction ON/OFF, Entrée = chat, F = filtre du journal, R = reconnexion
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class ClientArene : MonoBehaviour
{
    const int PORT = 5050;
    const float HAUTEUR_VIRTUELLE = 700f;
    const float LARGEUR_PANNEAU = 430f, HAUT_BARRE = 44f, BAS_BARRE = 34f;
    static readonly CultureInfo INV = CultureInfo.InvariantCulture;

    static ClientArene instance;
    string hote = "127.0.0.1";

    // ------------------------------------------------- règles (envoyées par le serveur)
    double areneL = 1200, areneH = 800, rayon = 20, vitesse = 260;
    int tickSim = 30, pvMax = 100;
    readonly List<double[]> murs = new List<double[]>();

    // ---------------------------------------------------------------- réseau
    TcpClient tcp;
    NetworkStream flux;
    int generation;
    volatile string etatConnexion = "menu";
    readonly ConcurrentQueue<string[]> boite = new ConcurrentQueue<string[]>();
    readonly object verrouEnvoi = new object();
    long envoyes, recus, octetsEnvoyes, octetsRecus;
    double dureeHandshakeMs = -1;
    string adresseLocale = "-";
    readonly Stopwatch chrono = Stopwatch.StartNew();
    double pingMs = -1;
    readonly List<float> pings = new List<float>();
    float dernierPing;

    // ---------------------------------------------------------- état du jeu
    class Joueur
    {
        public string nom = "?";
        public double x, y, a;            // dernières valeurs reçues du serveur
        public float ax, ay;              // position affichée (interpolée)
        public int pv = 100, score, morts;
        public bool vivant = true, bot;
        public float r;                   // secondes avant réapparition
        public Color couleur = Color.white;
        public float flash;               // moment du dernier dégât reçu
        public GameObject corps, contour, canon;   // version simple (ronds)
        public GameObject tenue, perso;            // version personnages (2 calques)
        public string skin = "soldat";
    }

    class Balle
    {
        public double x, y, vx, vy;
        public float tRecu;
        public GameObject go;
    }

    class Entree { public int seq, dx, dy; public double angle; public bool tir; }

    class Effet { public Vector2 pos; public Color couleur; public float debut; public GameObject go; }

    readonly Dictionary<int, Joueur> joueurs = new Dictionary<int, Joueur>();
    readonly Dictionary<int, Balle> balles = new Dictionary<int, Balle>();
    readonly List<Entree> enAttente = new List<Entree>();      // entrées pas encore confirmées
    readonly List<Effet> effets = new List<Effet>();
    int monId = -1;
    string monNom = "?";
    Color maCouleur = Color.white;
    double predX, predY;                  // MA position prédite
    float monAx, monAy;                   // ma position affichée
    double monAngle;
    int seq;
    float accumulateur;
    bool prediction = true;
    float derniereCorrection, correctionMax;
    string tueurDeMoi = "";

    // ---------------------------------------------------------------- menu / personnages
    static readonly string[] SKINS = { "soldat", "agent", "survivante", "casquette", "ninja", "robot" };
    string pseudo = "Joueur";
    string skinChoisi = "soldat";
    Sprite spriteTenue;
    readonly Dictionary<string, Sprite> spritesPerso = new Dictionary<string, Sprite>();
    bool persosDisponibles;

    // ---------------------------------------------------------------- manche (mode match)
    int manche = 1, limite = 10;
    string modeJeu = "", niveauBots = "";
    bool veutRegarder;                 // true = spectateur (on regarde sans jouer)
    static readonly Dictionary<string, string> NOMS_MODES = new Dictionary<string, string>
    {
        {"bots", "Toi contre les bots"}, {"amis", "Entre amis"}, {"demo", "Démo : les bots entre eux"},
    };
    string phase = "jeu", gagnant = "";
    float resteFin;

    // ------------------------------------------------------------- affichage
    class Anim { public string type; public bool versServeur; public float debut, duree; }
    readonly List<Anim> anims = new List<Anim>();
    readonly List<KeyValuePair<string, Color>> journal = new List<KeyValuePair<string, Color>>();
    readonly List<KeyValuePair<string, float>> fil = new List<KeyValuePair<string, float>>();  // fil des éliminations
    readonly Dictionary<int, KeyValuePair<string, float>> bulles = new Dictionary<int, KeyValuePair<string, float>>();
    bool toutAfficher;
    string saisie;
    int frameOuvertureChat;
    float debitEnvoyes, debitRecus, debitOEnvoyes, debitORecus, tDebit;
    long[] precedent = new long[4];

    Texture2D blanc;
    Sprite spriteCarre, spriteRond, spriteAnneau;
    Material materielSprite;
    GameObject viseur;
    GUIStyle sTitre, sGras, sNormal, sPetit, sMono, sChamp, sCentre, sGrand;

    static readonly Color FOND = Rgb(18, 20, 28), FOND_PANNEAU = Rgb(26, 29, 40), BORDURE = Rgb(55, 60, 80);
    static readonly Color TEXTE = Rgb(225, 228, 235), GRIS = Rgb(140, 145, 160), VERT = Rgb(80, 220, 120), ROUGE = Rgb(255, 90, 90);
    static readonly Dictionary<string, Color> COULEURS = new Dictionary<string, Color>
    {
        {"SYN", Rgb(255,255,255)}, {"SYN-ACK", Rgb(200,200,200)}, {"ACK", Rgb(255,255,255)},
        {"bienvenue", Rgb(120,230,255)}, {"entree", Rgb(90,150,255)}, {"etat", Rgb(80,210,130)},
        {"ping", Rgb(255,215,80)}, {"pong", Rgb(255,160,50)}, {"chat", Rgb(255,120,200)},
        {"evenement", Rgb(190,140,255)}, {"FIN", Rgb(255,90,90)}, {"info", Rgb(150,150,165)},
        {"erreur", Rgb(255,90,90)},
    };

    static Color Rgb(int r, int g, int b) { return new Color(r / 255f, g / 255f, b / 255f); }
    static Color CouleurType(string t) { Color c; return COULEURS.TryGetValue(t, out c) ? c : TEXTE; }
    static bool Frequent(string t) { return t == "entree" || t == "etat"; }
    static string Nb(double v) { return v.ToString("R", INV); }
    static string Nb(int v) { return v.ToString(INV); }
    static double Num(object o) { return Convert.ToDouble(o, INV); }
    static double Borner(double v, double a, double b) { return v < a ? a : (v > b ? b : v); }

    // ==================================================================== démarrage
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reinitialiser() { instance = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void CreerAutomatiquement()
    {
        if (instance == null) new GameObject("ClientArene").AddComponent<ClientArene>();
    }

    void Awake()
    {
        if (instance != null && instance != this) { enabled = false; Destroy(gameObject); return; }
        instance = this;
        Application.runInBackground = true;
        Application.targetFrameRate = 60;
    }

    void Start()
    {
        if (instance != this) return;
        blanc = new Texture2D(1, 1);
        blanc.SetPixel(0, 0, Color.white);
        blanc.Apply();
        spriteCarre = Sprite.Create(blanc, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
        spriteRond = CreerSpriteRond(64, 0f);
        spriteAnneau = CreerSpriteRond(64, 0.78f);
        Shader shader = Shader.Find("Sprites/Default");
        if (shader != null) materielSprite = new Material(shader);
        viseur = CreerSprite("Viseur", spriteAnneau, new Color(1, 1, 1, 0.8f), 50);
        viseur.transform.localScale = new Vector3(22, 22, 1);

        // personnages : images PNG intégrées dans ce fichier (pas de réglages d'import Unity)
        spriteTenue = ChargerSprite("tenue");
        foreach (string sk in SKINS)
        {
            Sprite sp = ChargerSprite(sk);
            if (sp != null) spritesPerso[sk] = sp;
        }
        persosDisponibles = spriteTenue != null && spritesPerso.ContainsKey("soldat");

        // préférences mémorisées d'une partie à l'autre
        pseudo = PlayerPrefs.GetString("pseudo", "Joueur");
        hote = PlayerPrefs.GetString("ip", "127.0.0.1");
        skinChoisi = PlayerPrefs.GetString("skin", "soldat");
        Journal("**", persosDisponibles ? "Personnages chargés (" + spritesPerso.Count + ", " + sourceImages + ")" : "Personnages introuvables : affichage simple", "info");
    }

    static string sourceImages = "";

    static Sprite ChargerSprite(string nom)
    {
        // 1) images importées par Unity : dossier Assets/Resources/Personnages (méthode la plus fiable)
        Texture2D importee = Resources.Load<Texture2D>("Personnages/" + nom);
        if (importee != null)
        {
            sourceImages = "dossier Resources";
            return Sprite.Create(importee, new Rect(0, 0, importee.width, importee.height), new Vector2(0.5f, 0.5f), importee.width);
        }
        // 2) sinon, images intégrées dans ce fichier
        sourceImages = "images intégrées";
        string b64;
        if (!ImagesPersos.PNG.TryGetValue(nom, out b64)) return null;
        // texture créée à la main : pleine résolution, sans compression ni mipmaps
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!tex.LoadImage(Convert.FromBase64String(b64))) return null;
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f),
                             tex.width, 0, SpriteMeshType.FullRect);
    }

    Sprite SpritePerso(string skin)
    {
        Sprite s;
        if (skin != null && spritesPerso.TryGetValue(skin, out s)) return s;
        return spritesPerso.TryGetValue("soldat", out s) ? s : null;
    }

    void Jouer(bool regarder = false)
    {
        veutRegarder = regarder;
        pseudo = pseudo.Trim();
        if (pseudo.Length == 0) pseudo = "Joueur";
        PlayerPrefs.SetString("pseudo", pseudo);
        PlayerPrefs.SetString("ip", hote.Trim());
        PlayerPrefs.SetString("skin", skinChoisi);
        PlayerPrefs.Save();
        GUIUtility.keyboardControl = 0;
        Connecter();
    }

    void RetourMenu()
    {
        generation++;            // ignore tout ce que l'ancienne connexion pourrait encore envoyer
        Fermer();
        ViderJeu();
        etatConnexion = "menu";
    }

    // Sprite rond (plein si trou = 0, anneau sinon)
    Sprite CreerSpriteRond(int t, float trou)
    {
        var tex = new Texture2D(t, t, TextureFormat.RGBA32, false);
        float r = t / 2f;
        for (int i = 0; i < t; i++)
            for (int j = 0; j < t; j++)
            {
                float d = Vector2.Distance(new Vector2(i + 0.5f, j + 0.5f), new Vector2(r, r));
                float alpha = Mathf.Clamp01(r - d);
                if (trou > 0) alpha *= Mathf.Clamp01(d - r * trou);
                tex.SetPixel(i, j, new Color(1, 1, 1, alpha));
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, t, t), new Vector2(0.5f, 0.5f), t);
    }

    GameObject CreerSprite(string nom, Sprite sprite, Color couleur, int ordre)
    {
        var go = new GameObject(nom);
        go.transform.SetParent(transform);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = couleur;
        sr.sortingOrder = ordre;
        if (materielSprite != null) sr.sharedMaterial = materielSprite;
        return go;
    }

    static void Couleur(GameObject go, Color c) { go.GetComponent<SpriteRenderer>().color = c; }

    // Coordonnées du serveur (y vers le bas) -> coordonnées Unity (y vers le haut)
    static Vector3 Monde(double x, double y) { return new Vector3((float)x, -(float)y, 0); }

    readonly List<GameObject> decor = new List<GameObject>();

    void ConstruireArene()
    {
        foreach (var go in decor) Destroy(go);
        decor.Clear();
        var sol = CreerSprite("Sol", spriteCarre, Rgb(22, 25, 36), -20);
        sol.transform.position = Monde(areneL / 2, areneH / 2);
        sol.transform.localScale = new Vector3((float)areneL, (float)areneH, 1);
        decor.Add(sol);
        // quadrillage
        for (int gx = 100; gx < areneL; gx += 100)
        {
            var l = CreerSprite("Grille", spriteCarre, Rgb(30, 34, 48), -19);
            l.transform.position = Monde(gx, areneH / 2);
            l.transform.localScale = new Vector3(2, (float)areneH, 1);
            decor.Add(l);
        }
        for (int gy = 100; gy < areneH; gy += 100)
        {
            var l = CreerSprite("Grille", spriteCarre, Rgb(30, 34, 48), -19);
            l.transform.position = Monde(areneL / 2, gy);
            l.transform.localScale = new Vector3((float)areneL, 2, 1);
            decor.Add(l);
        }
        // bords
        double[][] bords = {
            new double[] { areneL / 2, -4, areneL + 16, 8 }, new double[] { areneL / 2, areneH + 4, areneL + 16, 8 },
            new double[] { -4, areneH / 2, 8, areneH }, new double[] { areneL + 4, areneH / 2, 8, areneH } };
        foreach (var b in bords)
        {
            var go = CreerSprite("Bord", spriteCarre, Rgb(90, 100, 140), -5);
            go.transform.position = Monde(b[0], b[1]);
            go.transform.localScale = new Vector3((float)b[2], (float)b[3], 1);
            decor.Add(go);
        }
        // murs
        foreach (var m in murs)
        {
            var ombre = CreerSprite("Ombre", spriteCarre, new Color(0, 0, 0, 0.35f), -6);
            ombre.transform.position = Monde(m[0] + m[2] / 2 + 5, m[1] + m[3] / 2 + 5);
            ombre.transform.localScale = new Vector3((float)m[2], (float)m[3], 1);
            var go = CreerSprite("Mur", spriteCarre, Rgb(78, 86, 118), -5);
            go.transform.position = Monde(m[0] + m[2] / 2, m[1] + m[3] / 2);
            go.transform.localScale = new Vector3((float)m[2], (float)m[3], 1);
            decor.Add(ombre);
            decor.Add(go);
        }
    }

    // ============================================== MÊMES RÈGLES QUE LE SERVEUR (prédiction)
    bool Collision(double x, double y)
    {
        foreach (var m in murs)
        {
            double px = Borner(x, m[0], m[0] + m[2]), py = Borner(y, m[1], m[1] + m[3]);
            if ((x - px) * (x - px) + (y - py) * (y - py) < rayon * rayon) return true;
        }
        return false;
    }

    // Copie exacte de deplacer() dans serveur_arene.py
    void Deplacer(ref double x, ref double y, int dx, int dy, double dt)
    {
        double n = Math.Sqrt(dx * dx + dy * dy);
        if (n == 0) return;
        double vx = dx / n * vitesse * dt, vy = dy / n * vitesse * dt;
        double nx = Borner(x + vx, rayon, areneL - rayon);
        if (!Collision(nx, y)) x = nx;
        double ny = Borner(y + vy, rayon, areneH - rayon);
        if (!Collision(x, ny)) y = ny;
    }

    // ==================================================================== réseau
    void Connecter()
    {
        Fermer();
        generation++;
        int g = generation;
        string h = hote.Trim();
        string[] vide;
        while (boite.TryDequeue(out vide)) { }
        ViderJeu();
        etatConnexion = "connexion";
        dureeHandshakeMs = -1;
        adresseLocale = "-";
        Journal("**", "Connexion à " + h + ":" + PORT + "...", "info");
        var thread = new Thread(() => BoucleReseau(h, g));
        thread.IsBackground = true;
        thread.Start();
    }

    void BoucleReseau(string h, int g)
    {
        TcpClient client = new TcpClient();
        NetworkStream f;
        try
        {
            var sw = Stopwatch.StartNew();
            IAsyncResult res = client.BeginConnect(h, PORT, null, null);
            if (!res.AsyncWaitHandle.WaitOne(5000))
            {
                client.Close();
                throw new TimeoutException("délai dépassé (5 s)");
            }
            client.EndConnect(res);
            double ms = sw.Elapsed.TotalMilliseconds;
            client.NoDelay = true;
            f = client.GetStream();
            if (g != generation) { client.Close(); return; }
            tcp = client;
            flux = f;
            dureeHandshakeMs = ms;
            adresseLocale = client.Client.LocalEndPoint.ToString();
            etatConnexion = "connecte";
            boite.Enqueue(new[] { g.ToString(), "connecte", "", "0" });
        }
        catch (Exception e)
        {
            if (g != generation) return;
            etatConnexion = "echec";
            var se = e as SocketException;
            boite.Enqueue(new[] { g.ToString(), "echec", se != null ? se.SocketErrorCode.ToString() : e.Message, "0" });
            return;
        }

        var tampon = new List<byte>();
        var buf = new byte[8192];
        string raison = "serveur fermé (FIN)";
        try
        {
            while (true)
            {
                int n = f.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                for (int k = 0; k < n; k++)
                {
                    if (buf[k] == (byte)'\n')
                    {
                        if (tampon.Count > 0)
                        {
                            string ligne = Encoding.UTF8.GetString(tampon.ToArray());
                            int taille = tampon.Count + 1;
                            Interlocked.Increment(ref recus);
                            Interlocked.Add(ref octetsRecus, taille);
                            boite.Enqueue(new[] { g.ToString(), "message", ligne, taille.ToString() });
                        }
                        tampon.Clear();
                    }
                    else tampon.Add(buf[k]);
                }
            }
        }
        catch (Exception e)
        {
            var se = e.InnerException as SocketException;
            raison = (se != null && se.SocketErrorCode == SocketError.ConnectionReset)
                ? "coupure brutale (RST)" : "interrompue (" + e.GetType().Name + ")";
        }
        if (g == generation && etatConnexion == "connecte")
        {
            etatConnexion = "perdue";
            boite.Enqueue(new[] { g.ToString(), "perdue", raison, "0" });
        }
    }

    bool Envoyer(string json, string type, string resume)
    {
        if (etatConnexion != "connecte" || flux == null) return false;
        byte[] d = Encoding.UTF8.GetBytes(json + "\n");
        try { lock (verrouEnvoi) flux.Write(d, 0, d.Length); }
        catch (Exception) { return false; }
        envoyes++;
        octetsEnvoyes += d.Length;
        Journal("->", resume, type, d.Length);
        anims.Add(new Anim { type = type, versServeur = true, debut = Time.time, duree = 0.4f });
        return true;
    }

    void Fermer()
    {
        etatConnexion = "fermee";
        try
        {
            if (tcp != null)
            {
                tcp.Client.Shutdown(SocketShutdown.Both);
                tcp.Close();
            }
        }
        catch (Exception) { }
        tcp = null;
        flux = null;
    }

    void OnDestroy() { if (instance == this) Fermer(); }
    void OnApplicationQuit() { Fermer(); }

    // ============================================================ messages reçus
    void TraiterBoite()
    {
        string[] e;
        while (boite.TryDequeue(out e))
        {
            if (e[0] != generation.ToString()) continue;
            switch (e[1])
            {
                case "connecte":
                    Journal("**", "Poignée de main TCP réussie en " + dureeHandshakeMs.ToString("0.0", INV) + " ms", "ACK");
                    Journal("**", "Mon adresse : " + adresseLocale, "info");
                    string[] etapes = { "SYN", "SYN-ACK", "ACK" };
                    for (int i = 0; i < 3; i++)
                        anims.Add(new Anim { type = etapes[i], versServeur = i != 1, debut = Time.time + i * 0.45f, duree = 0.45f });
                    break;
                case "echec":
                    Journal("**", "Échec connexion : " + e[2], "erreur");
                    break;
                case "perdue":
                    Journal("**", "Déconnecté : " + e[2], "FIN");
                    anims.Add(new Anim { type = "FIN", versServeur = false, debut = Time.time, duree = 0.5f });
                    break;
                case "message":
                    Dictionary<string, object> msg;
                    try { msg = MiniJson.Parse(e[2]) as Dictionary<string, object>; }
                    catch (Exception ex) { Journal("**", "JSON invalide : " + ex.Message, "erreur"); continue; }
                    if (msg != null)
                    {
                        try { Recevoir(msg, int.Parse(e[3])); }
                        catch (Exception ex) { Journal("**", "Message mal formé : " + ex.Message, "erreur"); }
                    }
                    break;
            }
        }
    }

    static Color CouleurJson(object o)
    {
        var l = o as List<object>;
        if (l == null || l.Count < 3) return Color.white;
        return new Color((float)Num(l[0]) / 255f, (float)Num(l[1]) / 255f, (float)Num(l[2]) / 255f);
    }

    static string Resumer(Dictionary<string, object> m)
    {
        object t;
        string type = m.TryGetValue("type", out t) ? t as string : "?";
        switch (type)
        {
            case "etat":
                return "etat (" + ((Dictionary<string, object>)m["joueurs"]).Count + " joueurs, "
                       + ((List<object>)m["balles"]).Count + " balles)";
            case "bienvenue": return "bienvenue id=" + m["id"] + " + règles du jeu";
            case "chat": return "chat " + m["nom"] + " : \"" + m["texte"] + "\"";
            case "evenement": return "evenement : " + m["texte"];
            default: return type;
        }
    }

    void Recevoir(Dictionary<string, object> msg, int taille)
    {
        string type = msg.ContainsKey("type") ? msg["type"] as string : "?";
        Journal("<-", Resumer(msg), type, taille);
        anims.Add(new Anim { type = type, versServeur = false, debut = Time.time, duree = 0.4f });

        switch (type)
        {
            case "bienvenue":
                monId = (int)Num(msg["id"]);
                monNom = msg["nom"] as string;
                maCouleur = CouleurJson(msg["couleur"]);
                var ar = (List<object>)msg["arene"];
                areneL = Num(ar[0]);
                areneH = Num(ar[1]);
                rayon = Num(msg["rayon"]);
                vitesse = Num(msg["vitesse"]);
                tickSim = (int)Num(msg["tick"]);
                pvMax = (int)Num(msg["pv_max"]);
                murs.Clear();
                foreach (object o in (List<object>)msg["murs"])
                {
                    var l = (List<object>)o;
                    murs.Add(new[] { Num(l[0]), Num(l[1]), Num(l[2]), Num(l[3]) });
                }
                predX = Num(msg["x"]);
                predY = Num(msg["y"]);
                monAx = (float)predX;
                monAy = (float)predY;
                ConstruireArene();
                // on se présente : pseudo + personnage (le serveur vérifie et l'annonce à tous)
                Envoyer("{\"type\":\"profil\",\"nom\":\"" + MiniJson.Echapper(pseudo) + "\",\"skin\":\"" + skinChoisi + "\",\"spectateur\":" + (veutRegarder ? "true" : "false") + "}",
                        "profil", "profil \"" + pseudo + "\" perso=" + skinChoisi);
                Journal("**", "Je suis " + monNom + ". Règles reçues : " + murs.Count + " murs, " + tickSim + " ticks/s", "bienvenue");
                break;

            case "etat":
                RecevoirEtat(msg);
                break;

            case "pong":
                pingMs = (chrono.Elapsed.TotalSeconds - Num(msg["t"])) * 1000.0;
                pings.Add((float)pingMs);
                if (pings.Count > 60) pings.RemoveAt(0);
                break;

            case "chat":
                bulles[(int)Num(msg["id"])] = new KeyValuePair<string, float>(msg["texte"] as string, Time.time);
                break;

            case "evenement":
                fil.Add(new KeyValuePair<string, float>(msg["texte"] as string, Time.time));
                if (fil.Count > 6) fil.RemoveAt(0);
                object genre;
                if (msg.TryGetValue("genre", out genre) && (genre as string) == "elimination")
                {
                    int victime = (int)Num(msg["victime"]);
                    Joueur jv;
                    Color c = joueurs.TryGetValue(victime, out jv) ? jv.couleur : Color.white;
                    AjouterExplosion(new Vector2((float)Num(msg["x"]), (float)Num(msg["y"])), c);
                    if (victime == monId)
                    {
                        Joueur jt;
                        int tueur = (int)Num(msg["tueur"]);
                        tueurDeMoi = joueurs.TryGetValue(tueur, out jt) ? jt.nom : "?";
                    }
                }
                break;
        }
    }

    void RecevoirEtat(Dictionary<string, object> msg)
    {
        // ---- joueurs ----
        var liste = (Dictionary<string, object>)msg["joueurs"];
        var vus = new HashSet<int>();
        foreach (var kv in liste)
        {
            int id = int.Parse(kv.Key, INV);
            var inf = (Dictionary<string, object>)kv.Value;
            vus.Add(id);
            Joueur j;
            if (!joueurs.TryGetValue(id, out j))
            {
                j = new Joueur();
                j.ax = (float)Num(inf["x"]);
                j.ay = (float)Num(inf["y"]);
                if (persosDisponibles)
                {
                    // le personnage lui-même est dessiné dans OnGUI (DessinerPersonnages)
                    j.contour = CreerSprite("Contour " + id, spriteAnneau, Color.white, 1);
                }
                else
                {
                    j.contour = CreerSprite("Contour " + id, spriteRond, Color.white, 1);
                    j.corps = CreerSprite("Joueur " + id, spriteRond, Color.white, 2);
                    j.canon = CreerSprite("Canon " + id, spriteCarre, Color.white, 3);
                }
                joueurs[id] = j;
            }
            int ancienPv = j.pv;
            bool etaitVivant = j.vivant;
            j.x = Num(inf["x"]);
            j.y = Num(inf["y"]);
            j.a = Num(inf["a"]);
            j.pv = (int)Num(inf["pv"]);
            j.score = (int)Num(inf["score"]);
            j.morts = (int)Num(inf["morts"]);
            j.vivant = (bool)inf["vivant"];
            j.r = (float)Num(inf["r"]);
            j.bot = (bool)inf["bot"];
            j.nom = inf["nom"] as string;
            j.couleur = CouleurJson(inf["couleur"]);
            object sk;
            string nouveauSkin = inf.TryGetValue("skin", out sk) && sk is string ? (string)sk : "soldat";
            if (nouveauSkin != j.skin && j.perso != null) j.perso.GetComponent<SpriteRenderer>().sprite = SpritePerso(nouveauSkin);
            j.skin = nouveauSkin;
            if (j.pv < ancienPv && j.vivant) j.flash = Time.time;
            // réapparition : on "téléporte" l'affichage au lieu de glisser à travers la carte
            if (j.vivant && !etaitVivant) { j.ax = (float)j.x; j.ay = (float)j.y; }

            if (id == monId) Reconcilier(j, (int)Num(inf["seq"]), etaitVivant);
        }
        var aRetirer = new List<int>();
        foreach (int id in joueurs.Keys) if (!vus.Contains(id)) aRetirer.Add(id);
        foreach (int id in aRetirer)
        {
            var j = joueurs[id];
            DetruireVisuels(j);
            joueurs.Remove(id);
        }

        // ---- manche (mode match) ----
        object mo;
        if (msg.TryGetValue("manche", out mo) && mo is Dictionary<string, object>)
        {
            var md = (Dictionary<string, object>)mo;
            manche = (int)Num(md["numero"]);
            phase = md["phase"] as string;
            limite = (int)Num(md["limite"]);
            gagnant = md["gagnant"] as string;
            object mj, nv;
            modeJeu = md.TryGetValue("mode", out mj) ? mj as string : "";
            niveauBots = md.TryGetValue("niveau", out nv) ? nv as string : "";
            resteFin = (float)Num(md["reste"]);
        }

        // ---- balles ----
        var vuesB = new HashSet<int>();
        foreach (object o in (List<object>)msg["balles"])
        {
            var l = (List<object>)o;
            int id = (int)Num(l[0]);
            vuesB.Add(id);
            Balle b;
            if (!balles.TryGetValue(id, out b))
            {
                b = new Balle();
                Joueur proprio;
                Color c = joueurs.TryGetValue((int)Num(l[5]), out proprio) ? proprio.couleur : Color.yellow;
                b.go = CreerSprite("Balle " + id, spriteRond, Color.Lerp(c, Color.white, 0.55f), 4);
                b.go.transform.localScale = new Vector3(10, 10, 1);
                balles[id] = b;
            }
            b.x = Num(l[1]); b.y = Num(l[2]); b.vx = Num(l[3]); b.vy = Num(l[4]);
            b.tRecu = Time.time;
        }
        var balAretirer = new List<int>();
        foreach (int id in balles.Keys) if (!vuesB.Contains(id)) balAretirer.Add(id);
        foreach (int id in balAretirer) { Destroy(balles[id].go); balles.Remove(id); }
    }

    // RÉCONCILIATION : le serveur a traité nos entrées jusqu'à "seqServeur".
    void Reconcilier(Joueur moi, int seqServeur, bool etaitVivant)
    {
        enAttente.RemoveAll(en => en.seq <= seqServeur);       // ces entrées sont confirmées
        double avantX = predX, avantY = predY;
        predX = moi.x;                                       // on repart de la VÉRITÉ du serveur
        predY = moi.y;
        if (!moi.vivant) { enAttente.Clear(); return; }
        if (!etaitVivant) { monAx = (float)predX; monAy = (float)predY; return; }
        if (prediction)
        {
            foreach (var e in enAttente)                     // ...et on rejoue ce qu'il n'a pas encore vu
                Deplacer(ref predX, ref predY, e.dx, e.dy, 1.0 / tickSim);
            derniereCorrection = (float)Math.Sqrt((predX - avantX) * (predX - avantX) + (predY - avantY) * (predY - avantY));
            correctionMax = Mathf.Max(correctionMax * 0.98f, derniereCorrection);
        }
    }

    static void DetruireVisuels(Joueur j)
    {
        foreach (var go in new[] { j.corps, j.contour, j.canon, j.tenue, j.perso })
            if (go != null) Destroy(go);
    }

    void AjouterExplosion(Vector2 pos, Color c)
    {
        var go = CreerSprite("Explosion", spriteAnneau, c, 10);
        go.transform.position = Monde(pos.x, pos.y);
        effets.Add(new Effet { pos = pos, couleur = c, debut = Time.time, go = go });
    }

    void ViderJeu()
    {
        foreach (var j in joueurs.Values) DetruireVisuels(j);
        joueurs.Clear();
        foreach (var b in balles.Values) Destroy(b.go);
        balles.Clear();
        foreach (var e in effets) Destroy(e.go);
        effets.Clear();
        enAttente.Clear();
        monId = -1;
        monNom = "?";
        maCouleur = Color.white;
        pingMs = -1;
        pings.Clear();
        bulles.Clear();
        fil.Clear();
        saisie = null;
        anims.Clear();
        tueurDeMoi = "";
        phase = "jeu";
        gagnant = "";
    }

    void Journal(string sens, string texte, string type, int octets = 0)
    {
        if (Frequent(type) && !toutAfficher) return;
        string ligne = DateTime.Now.ToString("HH:mm:ss.fff", INV) + " " + sens + " " + texte;
        if (octets > 0) ligne += " (" + octets + " o)";
        journal.Add(new KeyValuePair<string, Color>(ligne, CouleurType(type)));
        if (journal.Count > 300) journal.RemoveAt(0);
    }

    // ==================================================================== clavier / souris
    enum Touche { Gauche, Droite, Haut, Bas, Tir, Entree, F, R, P, Echap }

    static bool Maintenue(Touche t)
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null) return false;
        // les touches de l'Input System sont des POSITIONS : wKey = Z sur un clavier AZERTY
        switch (t)
        {
            case Touche.Gauche: return kb.leftArrowKey.isPressed || kb.aKey.isPressed;
            case Touche.Droite: return kb.rightArrowKey.isPressed || kb.dKey.isPressed;
            case Touche.Haut: return kb.upArrowKey.isPressed || kb.wKey.isPressed;
            case Touche.Bas: return kb.downArrowKey.isPressed || kb.sKey.isPressed;
            case Touche.Tir: return kb.spaceKey.isPressed || (Mouse.current != null && Mouse.current.leftButton.isPressed);
        }
        return false;
#else
        switch (t)
        {
            case Touche.Gauche: return Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.A);
            case Touche.Droite: return Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D);
            case Touche.Haut: return Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.Z) || Input.GetKey(KeyCode.W);
            case Touche.Bas: return Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S);
            case Touche.Tir: return Input.GetKey(KeyCode.Space) || Input.GetMouseButton(0);
        }
        return false;
#endif
    }

    static bool Appuyee(Touche t)
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null) return false;
        switch (t)
        {
            case Touche.Entree: return kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
            case Touche.F: return kb.fKey.wasPressedThisFrame;
            case Touche.R: return kb.rKey.wasPressedThisFrame;
            case Touche.P: return kb.pKey.wasPressedThisFrame;
            case Touche.Echap: return kb.escapeKey.wasPressedThisFrame;
        }
        return false;
#else
        switch (t)
        {
            case Touche.Entree: return Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
            case Touche.F: return Input.GetKeyDown(KeyCode.F);
            case Touche.R: return Input.GetKeyDown(KeyCode.R);
            case Touche.P: return Input.GetKeyDown(KeyCode.P);
            case Touche.Echap: return Input.GetKeyDown(KeyCode.Escape);
        }
        return false;
#endif
    }

    static Vector2 PositionSouris()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
#else
        return Input.mousePosition;
#endif
    }

    // ==================================================================== Update
    void Update()
    {
        TraiterBoite();
        float maintenant = Time.time;
        bool connecte = etatConnexion == "connecte" && monId >= 0;
        bool clavierLibre = saisie == null && (connecte || GUIUtility.keyboardControl == 0);

        if (clavierLibre)
        {
            if (connecte && Appuyee(Touche.Entree)) { saisie = ""; frameOuvertureChat = Time.frameCount; }
            if (Appuyee(Touche.F)) toutAfficher = !toutAfficher;
            if (Appuyee(Touche.P))
            {
                prediction = !prediction;
                Journal("**", "Prédiction côté client : " + (prediction ? "ACTIVÉE" : "DÉSACTIVÉE"), "info");
            }
            if (Appuyee(Touche.R) && (etatConnexion == "perdue" || etatConnexion == "echec")) Connecter();
            if (connecte && Appuyee(Touche.Echap)) { RetourMenu(); return; }
        }

        Camera cam = AjusterCamera();

        // ---- entrées : un "pas" toutes les 1/30 s, exactement comme la simulation du serveur ----
        if (connecte)
        {
            accumulateur += Time.deltaTime;
            float pas = 1f / tickSim;
            int nbPas = 0;
            while (accumulateur >= pas && nbPas < 4)
            {
                accumulateur -= pas;
                nbPas++;
                if (joueurs.ContainsKey(monId)) PasEntree(cam, clavierLibre);   // spectateur : rien à envoyer
            }
            if (accumulateur > pas) accumulateur = 0;

            if (maintenant - dernierPing >= 1f)
            {
                dernierPing = maintenant;
                string rtt = pingMs >= 0 ? Nb((int)Math.Round(pingMs)) : "null";
                Envoyer("{\"type\":\"ping\",\"t\":" + Nb(chrono.Elapsed.TotalSeconds) + ",\"rtt\":" + rtt + "}", "ping", "ping");
            }
        }

        MettreAJourAffichage(cam, connecte);
        MettreAJourDebit(maintenant);
        anims.RemoveAll(a => maintenant > a.debut + a.duree);
        if (anims.Count > 300) anims.RemoveRange(0, anims.Count - 300);
        fil.RemoveAll(n => maintenant - n.Value > 6f);
    }

    void PasEntree(Camera cam, bool clavierLibre)
    {
        int dx = 0, dy = 0;
        bool tir = false;
        Joueur moi;
        bool vivant = joueurs.TryGetValue(monId, out moi) ? moi.vivant : true;
        if (clavierLibre)
        {
            if (Maintenue(Touche.Gauche)) dx -= 1;
            if (Maintenue(Touche.Droite)) dx += 1;
            if (Maintenue(Touche.Haut)) dy -= 1;
            if (Maintenue(Touche.Bas)) dy += 1;
            Vector2 souris = PositionSouris();
            bool dansJeu = cam != null && cam.pixelRect.Contains(souris);
            tir = Maintenue(Touche.Tir) && dansJeu;
            if (cam != null && dansJeu)
            {
                Vector3 w = cam.ScreenToWorldPoint(new Vector3(souris.x, souris.y, 10f));
                double sx = w.x, sy = -w.y;                     // souris en coordonnées serveur
                double ox = prediction ? predX : (moi != null ? moi.x : predX);
                double oy = prediction ? predY : (moi != null ? moi.y : predY);
                monAngle = Math.Atan2(sy - oy, sx - ox);
            }
        }
        seq++;
        // On envoie l'ENTRÉE (pas la position !)
        Envoyer("{\"type\":\"entree\",\"seq\":" + Nb(seq) + ",\"dx\":" + Nb(dx) + ",\"dy\":" + Nb(dy)
                + ",\"angle\":" + Nb(Math.Round(monAngle, 4)) + ",\"tir\":" + (tir ? "true" : "false") + "}",
                "entree", "entree #" + seq + " dir=(" + dx + "," + dy + ")" + (tir ? " TIR" : ""));
        enAttente.Add(new Entree { seq = seq, dx = dx, dy = dy, angle = monAngle, tir = tir });
        if (enAttente.Count > 200) enAttente.RemoveAt(0);
        // PRÉDICTION : on applique l'entrée tout de suite chez nous
        if (prediction && vivant) Deplacer(ref predX, ref predY, dx, dy, 1.0 / tickSim);
    }

    void MettreAJourAffichage(Camera cam, bool connecte)
    {
        float maintenant = Time.time;
        float f = Mathf.Min(1f, Time.deltaTime * 15f);
        foreach (var kv in joueurs)
        {
            Joueur j = kv.Value;
            bool moi = kv.Key == monId;
            double cx, cy, angle;
            if (moi)
            {
                // avec prédiction : notre position calculée localement ; sans : celle du serveur (en retard)
                double bx = prediction ? predX : j.x, by = prediction ? predY : j.y;
                float g = prediction ? Mathf.Min(1f, Time.deltaTime * 25f) : f;
                monAx += ((float)bx - monAx) * g;
                monAy += ((float)by - monAy) * g;
                cx = monAx; cy = monAy;
                angle = monAngle;
            }
            else
            {
                // interpolation : les autres glissent vers la dernière position reçue
                j.ax += ((float)j.x - j.ax) * f;
                j.ay += ((float)j.y - j.ay) * f;
                cx = j.ax; cy = j.ay;
                angle = j.a;
            }
            bool visible = j.vivant && connecte;
            bool touche = maintenant - j.flash < 0.12f;
            if (persosDisponibles)
            {
                // --- personnage : dessiné dans OnGUI ; ici seulement l'anneau sous MON perso ---
                j.contour.SetActive(visible && moi);
                if (!visible) continue;
                float da = (float)(rayon * 2 + 18);
                j.contour.transform.position = Monde(cx, cy);
                j.contour.transform.localScale = new Vector3(da, da, 1);
                Couleur(j.contour, new Color(1, 1, 1, 0.45f));
                continue;
            }
            // --- version simple : un rond + un canon ---
            j.corps.SetActive(visible);
            j.contour.SetActive(visible);
            j.canon.SetActive(visible);
            if (!visible) continue;
            float d = (float)(rayon * 2);
            Couleur(j.corps, touche ? Color.white : j.couleur);
            Couleur(j.contour, moi ? Color.white : new Color(0, 0, 0, 0.5f));
            j.corps.transform.position = Monde(cx, cy);
            j.corps.transform.localScale = new Vector3(d, d, 1);
            j.contour.transform.position = Monde(cx, cy);
            j.contour.transform.localScale = new Vector3(d + 6, d + 6, 1);
            float lc = (float)rayon * 1.2f;
            j.canon.transform.position = Monde(cx + Math.Cos(angle) * rayon * 0.9, cy + Math.Sin(angle) * rayon * 0.9);
            j.canon.transform.rotation = Quaternion.Euler(0, 0, -(float)(angle * Mathf.Rad2Deg));
            j.canon.transform.localScale = new Vector3(lc, 9, 1);
            Couleur(j.canon, Color.Lerp(j.couleur, Color.black, 0.35f));
        }

        // balles : extrapolation entre deux états (position + vitesse × temps écoulé)
        foreach (var b in balles.Values)
        {
            float dt = Mathf.Min(0.15f, maintenant - b.tRecu);
            b.go.transform.position = Monde(b.x + b.vx * dt, b.y + b.vy * dt);
            b.go.SetActive(connecte);
        }

        // explosions
        for (int i = effets.Count - 1; i >= 0; i--)
        {
            var e = effets[i];
            float p = (maintenant - e.debut) / 0.5f;
            if (p >= 1f) { Destroy(e.go); effets.RemoveAt(i); continue; }
            float t = Mathf.Lerp(20f, 110f, p);
            e.go.transform.localScale = new Vector3(t, t, 1);
            Couleur(e.go, new Color(e.couleur.r, e.couleur.g, e.couleur.b, 1f - p));
        }

        // viseur
        Vector2 souris = PositionSouris();
        bool dansJeu = cam != null && connecte && joueurs.ContainsKey(monId) && cam.pixelRect.Contains(souris);
        viseur.SetActive(dansJeu);
        if (dansJeu)
        {
            Vector3 w = cam.ScreenToWorldPoint(new Vector3(souris.x, souris.y, 10f));
            viseur.transform.position = new Vector3(w.x, w.y, 0);
        }
    }

    void MettreAJourDebit(float maintenant)
    {
        if (maintenant - tDebit < 1f) return;
        float dt = maintenant - tDebit;
        long[] actuel = { envoyes, Interlocked.Read(ref recus), octetsEnvoyes, Interlocked.Read(ref octetsRecus) };
        debitEnvoyes = (actuel[0] - precedent[0]) / dt;
        debitRecus = (actuel[1] - precedent[1]) / dt;
        debitOEnvoyes = (actuel[2] - precedent[2]) / dt;
        debitORecus = (actuel[3] - precedent[3]) / dt;
        precedent = actuel;
        tDebit = maintenant;
    }

    Camera AjusterCamera()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            cam = go.AddComponent<Camera>();
        }
        float s = Screen.height / HAUTEUR_VIRTUELLE;
        float panneau = LARGEUR_PANNEAU * s, haut = HAUT_BARRE * s, bas = BAS_BARRE * s;
        float largeurJeu = Mathf.Max(10f, Screen.width - panneau);
        float hauteurJeu = Mathf.Max(10f, Screen.height - haut - bas);
        cam.rect = new Rect(0, bas / Screen.height, largeurJeu / Screen.width, hauteurJeu / Screen.height);
        cam.orthographic = true;
        float aspect = largeurJeu / hauteurJeu;
        float marge = 20f;
        cam.orthographicSize = Mathf.Max((float)areneH / 2f + marge, ((float)areneL / 2f + marge) / aspect);
        cam.transform.position = new Vector3((float)areneL / 2f, -(float)areneH / 2f, -10f);
        cam.transform.rotation = Quaternion.identity;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = FOND;
        return cam;
    }

    // ==================================================================== interface (OnGUI)
    void Styles()
    {
        if (sNormal != null) return;
        sNormal = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = false, clipping = TextClipping.Clip };
        sNormal.normal.textColor = Color.white;
        sGras = new GUIStyle(sNormal) { fontSize = 14, fontStyle = FontStyle.Bold };
        sTitre = new GUIStyle(sNormal) { fontSize = 20, fontStyle = FontStyle.Bold };
        sGrand = new GUIStyle(sNormal) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        sPetit = new GUIStyle(sNormal) { fontSize = 11 };
        sCentre = new GUIStyle(sPetit) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        sMono = new GUIStyle(sNormal) { fontSize = 11 };
        Font menlo = Font.CreateDynamicFontFromOSFont(new[] { "Menlo", "Consolas", "Courier New" }, 11);
        if (menlo != null) sMono.font = menlo;
        sChamp = new GUIStyle(GUI.skin.textField) { fontSize = 13 };
    }

    void Rectangle(Rect r, Color c)
    {
        Color avant = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, blanc);
        GUI.color = avant;
    }

    void Texte(Rect r, string t, GUIStyle s, Color c)
    {
        Color avant = GUI.color;
        GUI.color = c;
        GUI.Label(r, t, s);
        GUI.color = avant;
    }

    void OnGUI()
    {
        if (blanc == null) return;
        Styles();
        float s = Screen.height / HAUTEUR_VIRTUELLE;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
        float W = Screen.width / s, H = HAUTEUR_VIRTUELLE;
        float x0 = W - LARGEUR_PANNEAU + 10;
        float largeurJeu = W - LARGEUR_PANNEAU;
        bool connecte = etatConnexion == "connecte" && monId >= 0;

        // --- menu : Entrée = jouer ---
        Event ev = Event.current;
        bool dansMenu = !connecte && etatConnexion != "connexion" && etatConnexion != "connecte";
        if (dansMenu && ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter))
        {
            ev.Use();
            Jouer();
        }

        // --- chat : Entrée / Échap ---
        if (saisie != null && ev.type == EventType.KeyDown && Time.frameCount - frameOuvertureChat > 1)
        {
            if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
            {
                string t = saisie.Trim();
                if (t.Length > 0)
                    Envoyer("{\"type\":\"chat\",\"texte\":\"" + MiniJson.Echapper(t) + "\"}", "chat", "chat \"" + t + "\"");
                saisie = null;
                GUIUtility.keyboardControl = 0;
                ev.Use();
            }
            else if (ev.keyCode == KeyCode.Escape)
            {
                saisie = null;
                GUIUtility.keyboardControl = 0;
                ev.Use();
            }
        }

        // --- personnages (dessinés ici, en coordonnées écran) ---
        if (connecte) DessinerPersonnages(Camera.main, s);

        // --- barre du haut ---
        Rectangle(new Rect(0, 0, W, HAUT_BARRE), FOND_PANNEAU);
        Rectangle(new Rect(0, HAUT_BARRE - 1, W, 1), BORDURE);
        Texte(new Rect(14, 9, 200, 28), "ARÈNE EN LIGNE", sTitre, TEXTE);
        Color cEtat; string tEtat;
        if (connecte) { cEtat = VERT; tEtat = "Connecté à " + hote + ":" + PORT + " en tant que " + monNom; }
        else if (etatConnexion == "connexion" || etatConnexion == "connecte") { cEtat = Rgb(255, 190, 60); tEtat = "Connexion à " + hote + ":" + PORT + "..."; }
        else if (etatConnexion == "menu") { cEtat = GRIS; tEtat = "Pas encore connecté"; }
        else { cEtat = ROUGE; tEtat = "Déconnecté   (R pour se reconnecter)"; }
        Rectangle(new Rect(186, 16, 12, 12), cEtat);
        Texte(new Rect(206, 12, 460, 22), tEtat, sNormal, cEtat);
        Texte(new Rect(x0, 12, 140, 22), "Ping : " + (pingMs >= 0 ? pingMs.ToString("0", INV) + " ms" : "-"), sGras, TEXTE);
        Joueur moiJ = null;
        if (connecte && joueurs.TryGetValue(monId, out moiJ))
            Texte(new Rect(x0 + 120, 12, 300, 22), "PV " + moiJ.pv + "/" + pvMax + "   |   " + moiJ.score + " élim.   |   " + moiJ.morts + " morts", sGras, moiJ.couleur);

        Camera cam = Camera.main;

        // --- noms + barres de vie + bulles de chat ---
        if (cam != null && connecte)
        {
            foreach (var kv in joueurs)
            {
                Joueur j = kv.Value;
                if (!j.vivant) continue;
                bool moi = kv.Key == monId;
                Vector3 haut = moi ? Monde(monAx, monAy) : Monde(j.ax, j.ay);
                haut += new Vector3(0, (float)rayon, 0);
                Vector3 sp = cam.WorldToScreenPoint(haut);
                float vx = sp.x / s, vy = (Screen.height - sp.y) / s;
                float lb = 40f;
                Rectangle(new Rect(vx - lb / 2, vy - 9, lb, 5), new Color(0, 0, 0, 0.6f));
                float ratio = Mathf.Clamp01(j.pv / (float)pvMax);
                Rectangle(new Rect(vx - lb / 2, vy - 9, lb * ratio, 5), Color.Lerp(ROUGE, VERT, ratio));
                Texte(new Rect(vx - 60, vy - 26, 120, 16), j.nom + (moi ? " (toi)" : ""), sCentre, moi ? Color.white : TEXTE);
                KeyValuePair<string, float> b;
                if (bulles.TryGetValue(kv.Key, out b) && Time.time - b.Value < 5f)
                {
                    float l = Mathf.Min(260, 14 + b.Key.Length * 7.5f);
                    Rect cadre = new Rect(vx - l / 2, vy - 52, l, 22);
                    Rectangle(cadre, Rgb(240, 240, 245));
                    Texte(cadre, b.Key, new GUIStyle(sNormal) { alignment = TextAnchor.MiddleCenter }, Rgb(20, 20, 25));
                }
            }
        }

        // --- fil des événements (en haut à gauche) ---
        for (int i = 0; i < fil.Count; i++)
        {
            Rect r = new Rect(12, HAUT_BARRE + 50 + i * 21, 360, 19);
            Rectangle(r, new Color(0, 0, 0, 0.45f));
            Texte(new Rect(r.x + 6, r.y + 1, r.width - 8, r.height), fil[i].Key, sNormal, CouleurType("evenement"));
        }

        // --- bannière de la manche ---
        if (connecte)
        {
            string tm = "Manche " + manche + "  ·  " + (limite > 0 ? "premier à " + limite + " éliminations" : "sans limite");
            string nomMode;
            if (!NOMS_MODES.TryGetValue(modeJeu ?? "", out nomMode)) nomMode = "";
            bool ilYaDesBots = false;
            foreach (var jb in joueurs.Values) if (jb.bot) ilYaDesBots = true;
            string tm2 = (nomMode.Length > 0 ? "Mode : " + nomMode : "")
                       + (ilYaDesBots && niveauBots.Length > 0 ? "   ·   bots : " + niveauBots : "")
                       + (joueurs.Count > 0 && !joueurs.ContainsKey(monId) ? "   ·   tu es SPECTATEUR (Échap : menu)" : "");
            Rect rb = new Rect(largeurJeu / 2 - 260, HAUT_BARRE + 4, 520, 40);
            Rectangle(rb, new Color(0, 0, 0, 0.45f));
            Texte(new Rect(rb.x, rb.y + 1, rb.width, 20), tm, new GUIStyle(sGras) { alignment = TextAnchor.MiddleCenter }, Rgb(255, 215, 80));
            Texte(new Rect(rb.x, rb.y + 20, rb.width, 18), tm2, new GUIStyle(sNormal) { alignment = TextAnchor.MiddleCenter, fontSize = 12 }, TEXTE);
        }

        // --- écran de fin de manche ---
        if (connecte && phase == "fin")
        {
            Rect r = new Rect(largeurJeu / 2 - 240, H / 2 - 130, 480, 250);
            Rectangle(r, new Color(0, 0, 0, 0.8f));
            Rectangle(new Rect(r.x, r.y, r.width, 3), Rgb(255, 215, 80));
            bool moiGagne = moiJ != null && gagnant == monNom;
            Texte(new Rect(r.x, r.y + 14, r.width, 40), moiGagne ? "VICTOIRE !" : "FIN DE LA MANCHE", sGrand, moiGagne ? VERT : Rgb(255, 215, 80));
            Texte(new Rect(r.x, r.y + 56, r.width, 22), gagnant + " remporte la manche " + manche,
                  new GUIStyle(sGras) { alignment = TextAnchor.MiddleCenter }, TEXTE);
            var podium = new List<Joueur>(joueurs.Values);
            podium.Sort((a, b) => b.score != a.score ? b.score.CompareTo(a.score) : a.morts.CompareTo(b.morts));
            for (int i = 0; i < podium.Count && i < 5; i++)
            {
                float ly = r.y + 92 + i * 22;
                Rectangle(new Rect(r.x + 110, ly + 5, 12, 12), podium[i].couleur);
                Texte(new Rect(r.x + 130, ly, 180, 20), (i + 1) + ". " + podium[i].nom, sNormal, TEXTE);
                Texte(new Rect(r.x + 300, ly, 100, 20), podium[i].score + " élim. / " + podium[i].morts + " morts", sNormal, GRIS);
            }
            Texte(new Rect(r.x, r.yMax - 28, r.width, 20), "Nouvelle manche dans " + resteFin.ToString("0", INV) + " s",
                  new GUIStyle(sNormal) { alignment = TextAnchor.MiddleCenter }, GRIS);
        }

        // --- écran "éliminé" ---
        if (connecte && phase != "fin" && joueurs.TryGetValue(monId, out moiJ) && !moiJ.vivant)
        {
            Rect r = new Rect(largeurJeu / 2 - 220, H / 2 - 60, 440, 110);
            Rectangle(r, new Color(0, 0, 0, 0.7f));
            Texte(new Rect(r.x, r.y + 12, r.width, 40), "ÉLIMINÉ", sGrand, ROUGE);
            Texte(new Rect(r.x, r.y + 56, r.width, 20), "par " + tueurDeMoi + "  —  réapparition dans " + moiJ.r.ToString("0.0", INV) + " s",
                  new GUIStyle(sNormal) { alignment = TextAnchor.MiddleCenter }, TEXTE);
        }

        // --- menu de départ (pseudo, adresse du serveur, personnage) ---
        if (!connecte) DessinerMenu(largeurJeu, H);

        // --- barre du bas ---
        Rect bas = new Rect(0, H - BAS_BARRE, largeurJeu, BAS_BARRE);
        Rectangle(bas, FOND);
        if (saisie != null)
        {
            Texte(new Rect(14, H - 28, 80, 22), "Message :", sNormal, CouleurType("chat"));
            GUI.SetNextControlName("chat");
            saisie = GUI.TextField(new Rect(90, H - 29, bas.width - 110, 24), saisie, 60, sChamp);
            GUI.FocusControl("chat");
        }
        else
        {
            Texte(new Rect(14, H - 27, bas.width - 20, 22),
                  connecte ? "ZQSD/flèches : bouger  |  Souris : viser  |  Clic : tirer  |  P : prédiction  |  Entrée : chat  |  Échap : menu"
                            : "Choisis ton pseudo, ton personnage et l'adresse du serveur, puis JOUER (ou Entrée)",
                  sNormal, GRIS);
        }

        DessinerPanneau(x0, H);
    }

    // Dessine chaque personnage : la "tenue" teintée à la couleur du joueur, puis le perso par-dessus,
    // tournés selon l'angle de visée. (Dessin via OnGUI : fonctionne quel que soit le pipeline de rendu.)
    void DessinerPersonnages(Camera cam, float s)
    {
        if (!persosDisponibles || cam == null) return;
        Matrix4x4 matriceAvant = GUI.matrix;
        Color couleurAvant = GUI.color;
        float taille = (float)(rayon * 4.6);                  // taille du dessin dans le monde
        foreach (var kv in joueurs)
        {
            Joueur j = kv.Value;
            if (!j.vivant) continue;
            bool moi = kv.Key == monId;
            Vector3 centreMonde = moi ? Monde(monAx, monAy) : Monde(j.ax, j.ay);
            double angle = moi ? monAngle : j.a;
            Vector3 p1 = cam.WorldToScreenPoint(centreMonde);
            Vector3 p2 = cam.WorldToScreenPoint(centreMonde + new Vector3(taille, 0, 0));
            float t = p2.x - p1.x;                             // taille en pixels écran
            Vector2 centre = new Vector2(p1.x, Screen.height - p1.y);
            Rect r = new Rect(centre.x - t / 2, centre.y - t / 2, t, t);
            // rotation autour du centre du personnage (y vers le bas, comme le serveur)
            GUI.matrix = Matrix4x4.TRS(centre, Quaternion.Euler(0, 0, (float)(angle * Mathf.Rad2Deg)), Vector3.one)
                       * Matrix4x4.TRS(-centre, Quaternion.identity, Vector3.one);
            bool touche = Time.time - j.flash < 0.12f;
            GUI.color = touche ? Color.white : j.couleur;
            GUI.DrawTexture(r, spriteTenue.texture);
            Sprite sp = SpritePerso(j.skin);
            GUI.color = touche ? new Color(1f, 0.55f, 0.55f) : Color.white;
            if (sp != null) GUI.DrawTexture(r, sp.texture);
        }
        GUI.matrix = matriceAvant;
        GUI.color = couleurAvant;
    }

    void DessinerMenu(float largeurJeu, float H)
    {
        float cx = largeurJeu / 2f, cy = H / 2f;
        bool enCours = etatConnexion == "connexion" || etatConnexion == "connecte";
        Rect boite = new Rect(cx - 280, cy - 215, 560, 420);
        Rectangle(boite, new Color(0.07f, 0.08f, 0.12f, 0.96f));
        Rectangle(new Rect(boite.x, boite.y, boite.width, 3), Rgb(120, 150, 230));
        Texte(new Rect(boite.x, boite.y + 14, boite.width, 40), "ARÈNE EN LIGNE", sGrand, TEXTE);
        Texte(new Rect(boite.x, boite.y + 54, boite.width, 18), "Jeu multijoueur client-serveur en TCP",
              new GUIStyle(sNormal) { alignment = TextAnchor.MiddleCenter }, GRIS);

        float y = boite.y + 88;
        Texte(new Rect(boite.x + 40, y + 2, 170, 22), "Pseudo :", sGras, TEXTE);
        if (!enCours) pseudo = GUI.TextField(new Rect(boite.x + 220, y, 240, 26), pseudo, 12, sChamp);
        else Texte(new Rect(boite.x + 220, y + 2, 240, 22), pseudo, sNormal, TEXTE);
        y += 36;
        Texte(new Rect(boite.x + 40, y + 2, 175, 22), "Adresse IP du serveur :", sGras, TEXTE);
        if (!enCours) hote = GUI.TextField(new Rect(boite.x + 220, y, 240, 26), hote, 40, sChamp);
        else Texte(new Rect(boite.x + 220, y + 2, 240, 22), hote, sNormal, TEXTE);
        Texte(new Rect(boite.x + 220, y + 28, 320, 16), "127.0.0.1 = ce PC. Sinon : l'IP affichée par le serveur", sPetit, GRIS);
        y += 56;

        Texte(new Rect(boite.x + 40, y, 200, 22), "Personnage :", sGras, TEXTE);
        y += 26;
        float tc = 74f, ecart = 10f;
        float total = SKINS.Length * tc + (SKINS.Length - 1) * ecart;
        Color apercu = Rgb(99, 170, 255);
        for (int i = 0; i < SKINS.Length; i++)
        {
            Rect r = new Rect(cx - total / 2 + i * (tc + ecart), y, tc, tc);
            bool choisi = SKINS[i] == skinChoisi;
            Rectangle(r, choisi ? Rgb(120, 150, 230) : Rgb(45, 50, 68));
            Rectangle(new Rect(r.x + 2, r.y + 2, r.width - 4, r.height - 4), choisi ? Rgb(48, 58, 92) : Rgb(28, 32, 45));
            Sprite sp = SpritePerso(SKINS[i]);
            if (persosDisponibles && sp != null)
            {
                Rect ri = new Rect(r.x + 4, r.y + 4, r.width - 8, r.height - 8);
                Color avant = GUI.color;
                GUI.color = apercu;
                GUI.DrawTexture(ri, spriteTenue.texture, ScaleMode.ScaleToFit);
                GUI.color = Color.white;
                GUI.DrawTexture(ri, sp.texture, ScaleMode.ScaleToFit);
                GUI.color = avant;
            }
            Texte(new Rect(r.x - 6, r.yMax + 3, r.width + 12, 16), SKINS[i], sCentre, choisi ? TEXTE : GRIS);
            if (!enCours && GUI.Button(r, GUIContent.none, GUIStyle.none)) skinChoisi = SKINS[i];
        }
        y += tc + 30;

        string erreur = etatConnexion == "echec" ? "Serveur injoignable : vérifie l'IP et que serveur_arene.py est lancé."
                      : etatConnexion == "perdue" ? "La connexion au serveur a été perdue."
                      : "";
        Texte(new Rect(boite.x, y, boite.width, 20), erreur, new GUIStyle(sNormal) { alignment = TextAnchor.MiddleCenter }, ROUGE);
        y += 26;

        var styleBouton = new GUIStyle(GUI.skin.button) { fontSize = 16, fontStyle = FontStyle.Bold };
        if (enCours)
        {
            Texte(new Rect(boite.x, y, boite.width, 22), "Connexion à " + hote + ":" + PORT + "...",
                  new GUIStyle(sGras) { alignment = TextAnchor.MiddleCenter }, Rgb(255, 190, 60));
            if (GUI.Button(new Rect(cx - 60, y + 28, 120, 26), "Annuler")) RetourMenu();
        }
        else
        {
            if (GUI.Button(new Rect(cx - 215, y, 200, 38), "JOUER", styleBouton)) Jouer(false);
            if (GUI.Button(new Rect(cx + 15, y, 200, 38), "REGARDER", styleBouton)) Jouer(true);
            Texte(new Rect(boite.x, y + 42, boite.width, 16), "REGARDER = mode spectateur : tu vois la partie sans jouer",
                  new GUIStyle(sPetit) { alignment = TextAnchor.MiddleCenter }, GRIS);
        }
    }

    void DessinerPanneau(float x0, float H)
    {
        float largeur = LARGEUR_PANNEAU - 26;
        Rectangle(new Rect(x0 - 10, HAUT_BARRE, LARGEUR_PANNEAU, H - HAUT_BARRE), FOND_PANNEAU);
        Rectangle(new Rect(x0 - 10, HAUT_BARRE, 1, H - HAUT_BARRE), BORDURE);

        // --- échanges client <-> serveur ---
        Texte(new Rect(x0, 50, 300, 20), "Échanges client <-> serveur", sGras, GRIS);
        Rect moi = new Rect(x0 + 10, 78, 100, 34), serv = new Rect(x0 + largeur - 110, 78, 100, 34);
        Rectangle(new Rect(moi.xMax, 94, serv.x - moi.xMax, 2), Rgb(70, 75, 100));
        foreach (var a in anims)
        {
            if (Time.time < a.debut) continue;
            float p = Mathf.Clamp01((Time.time - a.debut) / a.duree);
            float xa = a.versServeur ? moi.xMax : serv.x, xb = a.versServeur ? serv.x : moi.xMax;
            float xp = Mathf.Lerp(xa, xb, p);
            Color c = CouleurType(a.type);
            float yp = a.versServeur ? 88 : 96;          // aller en haut, retour en bas
            if (Frequent(a.type)) Rectangle(new Rect(xp - 3, yp - 3, 6, 6), c);
            else
            {
                Rectangle(new Rect(xp - 6, yp - 6, 12, 12), c);
                Texte(new Rect(xp - 30, a.versServeur ? 66 : 104, 90, 16), a.type, sPetit, c);
            }
        }
        foreach (var bd in new[] { new KeyValuePair<Rect, string>(moi, "MOI (Unity)"), new KeyValuePair<Rect, string>(serv, "SERVEUR") })
        {
            Rect r = bd.Key;
            Rectangle(r, bd.Value == "SERVEUR" ? Rgb(120, 150, 230) : maCouleur);
            Rectangle(new Rect(r.x + 2, r.y + 2, r.width - 4, r.height - 4), Rgb(40, 45, 65));
            Texte(r, bd.Value, new GUIStyle(sGras) { alignment = TextAnchor.MiddleCenter }, TEXTE);
        }
        var centre = new GUIStyle(sPetit) { alignment = TextAnchor.MiddleCenter };
        Texte(new Rect(moi.x - 30, 116, 160, 14), adresseLocale, centre, GRIS);
        Texte(new Rect(serv.x - 30, 116, 160, 14), hote + ":" + PORT, centre, GRIS);

        // --- ping ---
        float y = 136;
        Texte(new Rect(x0, y, 400, 20), "Ping (aller-retour) : " + (pingMs >= 0 ? pingMs.ToString("0.0", INV) + " ms" : "-"), sGras, TEXTE);
        Rect graphe = new Rect(x0, y + 20, largeur, 34);
        Rectangle(graphe, Rgb(14, 16, 23));
        if (pings.Count > 0)
        {
            float maxi = 50f;
            foreach (float v in pings) maxi = Mathf.Max(maxi, v * 1.2f);
            float pasG = graphe.width / 60f;
            for (int i = 0; i < pings.Count; i++)
            {
                float h = Mathf.Max(1f, pings[i] / maxi * (graphe.height - 4));
                Rectangle(new Rect(graphe.x + i * pasG, graphe.yMax - 2 - h, Mathf.Max(1f, pasG - 1), h), CouleurType("ping"));
            }
            Texte(new Rect(graphe.xMax - 80, graphe.y + 1, 78, 14), "max " + maxi.ToString("0", INV) + " ms", sPetit, GRIS);
        }

        // --- débit ---
        y = 196;
        Texte(new Rect(x0, y, largeur, 18), "Envoyés : " + envoyes + " paquets  (" + debitEnvoyes.ToString("0", INV) + "/s, " + debitOEnvoyes.ToString("0", INV) + " o/s)", sNormal, CouleurType("entree"));
        Texte(new Rect(x0, y + 17, largeur, 18), "Reçus : " + Interlocked.Read(ref recus) + " paquets  (" + debitRecus.ToString("0", INV) + "/s, " + (debitORecus / 1024f).ToString("0.0", INV) + " Ko/s)", sNormal, CouleurType("etat"));
        string hs = dureeHandshakeMs >= 0 ? dureeHandshakeMs.ToString("0.0", INV) + " ms" : "-";
        Texte(new Rect(x0, y + 34, largeur, 18), "Poignée de main TCP : " + hs + "   |   simulation serveur " + tickSim + "/s", sNormal, GRIS);

        // --- prédiction / réconciliation ---
        y = 256;
        Rectangle(new Rect(x0, y - 4, largeur, 1), BORDURE);
        Texte(new Rect(x0, y, largeur, 20), "Prédiction côté client : " + (prediction ? "ACTIVÉE" : "DÉSACTIVÉE") + "  [P]", sGras, prediction ? VERT : ROUGE);
        Texte(new Rect(x0, y + 19, largeur, 18), "Entrées envoyées, pas encore confirmées : " + enAttente.Count, sNormal, TEXTE);
        Texte(new Rect(x0, y + 36, largeur, 18), "Correction après réconciliation : " + derniereCorrection.ToString("0.00", INV) + " px (max récent " + correctionMax.ToString("0.0", INV) + ")", sNormal, GRIS);
        // barre visuelle des entrées en attente
        for (int i = 0; i < Mathf.Min(enAttente.Count, 40); i++)
            Rectangle(new Rect(x0 + i * 10, y + 57, 8, 6), CouleurType("entree"));

        // --- classement ---
        y = 330;
        Rectangle(new Rect(x0, y - 4, largeur, 1), BORDURE);
        Texte(new Rect(x0, y, 200, 20), "Classement", sGras, GRIS);
        Texte(new Rect(x0 + 200, y + 2, 200, 16), "élim.   morts    PV", sPetit, GRIS);
        var classement = new List<KeyValuePair<int, Joueur>>(joueurs);
        classement.Sort((a, b) => b.Value.score != a.Value.score ? b.Value.score.CompareTo(a.Value.score) : a.Value.morts.CompareTo(b.Value.morts));
        for (int i = 0; i < classement.Count && i < 6; i++)
        {
            float ly = y + 20 + i * 16;
            var j = classement[i].Value;
            Rectangle(new Rect(x0, ly + 4, 10, 10), j.couleur);
            string nom = j.nom + (classement[i].Key == monId ? "  (toi)" : "") + (j.bot ? "  [bot]" : "");
            Texte(new Rect(x0 + 16, ly, 190, 16), nom, sNormal, classement[i].Key == monId ? Color.white : TEXTE);
            Texte(new Rect(x0 + 205, ly, 40, 16), j.score.ToString(INV), sNormal, TEXTE);
            Texte(new Rect(x0 + 250, ly, 40, 16), j.morts.ToString(INV), sNormal, GRIS);
            Texte(new Rect(x0 + 295, ly, 60, 16), j.vivant ? j.pv.ToString(INV) : "mort", sNormal, j.vivant ? TEXTE : ROUGE);
        }

        // --- journal ---
        y = 432;
        Rectangle(new Rect(x0, y - 4, largeur, 1), BORDURE);
        Texte(new Rect(x0, y, 140, 20), "Journal réseau", sGras, GRIS);
        Texte(new Rect(x0 + 125, y + 3, 280, 16), "entree/etat : " + (toutAfficher ? "affichés" : "masqués") + " [F]", sPetit, GRIS);
        float hl = 15f;
        int nb = (int)((H - 8 - (y + 22)) / hl);
        int debut = Mathf.Max(0, journal.Count - nb);
        for (int i = debut; i < journal.Count; i++)
            Texte(new Rect(x0, y + 22 + (i - debut) * hl, largeur, hl), journal[i].Key, sMono, journal[i].Value);
    }

    // ============================================================================
    // Mini lecteur JSON (JsonUtility de Unity ne sait pas lire les dictionnaires)
    static class MiniJson
    {
        public static object Parse(string s)
        {
            int i = 0;
            return Valeur(s, ref i);
        }

        static void Espaces(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object Valeur(string s, ref int i)
        {
            Espaces(s, ref i);
            char c = s[i];
            if (c == '{')
            {
                i++;
                var d = new Dictionary<string, object>();
                Espaces(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Espaces(s, ref i);
                    string cle = Chaine(s, ref i);
                    Espaces(s, ref i);
                    i++;                                    // ':'
                    d[cle] = Valeur(s, ref i);
                    Espaces(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    i++;                                    // '}'
                    return d;
                }
            }
            if (c == '[')
            {
                i++;
                var l = new List<object>();
                Espaces(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Valeur(s, ref i));
                    Espaces(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    i++;                                    // ']'
                    return l;
                }
            }
            if (c == '"') return Chaine(s, ref i);
            if (c == 't') { i += 4; return true; }
            if (c == 'f') { i += 5; return false; }
            if (c == 'n') { i += 4; return null; }
            int debut = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(debut, i - debut), CultureInfo.InvariantCulture);
        }

        static string Chaine(string s, ref int i)
        {
            i++;                                            // '"' ouvrant
            var sb = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;           // \" \\ \/
                }
            }
            i++;                                            // '"' fermant
            return sb.ToString();
        }

        public static string Echapper(string t)
        {
            var sb = new StringBuilder();
            foreach (char c in t)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < ' ') sb.Append("\\u" + ((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}

// =============================================================================
// Images des personnages (PNG 256x256 encodés en base64), vue de dessus, regard vers la droite.
//  - "tenue" : corps + bras en blanc, teinté avec la couleur du joueur
//  - les autres : arme, mains, tête et coiffure (couleurs fixes), dessinés par-dessus
// =============================================================================
static class ImagesPersos
{
    public static readonly System.Collections.Generic.Dictionary<string, string> PNG =
        new System.Collections.Generic.Dictionary<string, string>
    {
        { "tenue", "iVBORw0KGgoAAAANSUhEUgAAAQAAAAEACAYAAABccqhmAAA/TklEQVR42u29aZQc1ZUu+p0TQ2bkXJNKQrMQAiFmMJONhZnNaIwxpjHuRy9f9+0G29fD66Zft/u1vfB1c+3V9123+9rXNB76XT+6wTYtPCDEbJp5EggJJEADg6RSzTlnRJxz3o+ISIVSGZGRWVmiqnS+tXJVVVZWVlZl7G9/ezh7AxISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhISEhIzGkT+CyQ+oGtJyH+ZJAAJea1IgpAEICGvDUkK8k2WmC3XApkBBi7JQBKAxCF4/8khuIbEFA1dkoEkAIkuveck4v1kmr2/cH+HaNPYJRlIApBo870mLe4jHfxMu0ba6j4xDcpBXhTyXyANv8nXpA0yINPg+aMQQFRlIIlAEoB8b6dg9KTNn2vX+P0yv5nBtyIESQSSACS6ZPj++0jEx3ZCBlENvfG+dgiik7BBXiwSc9rwgww56GvSATEEvR7RBhG0ul80IYCooYMkAkkAh8X7GMVbkxb3kQiPj5IwjOr92zF20eLnp5pElAQgMecMP6rBe/fRCMQwlRAgSOKLkK9b3RemIiQRSAI47Iw/irE3Gn0YKTR7vPB99BNHM0PzEn/clwBsx/jDblHUQ7tVBEkAEjP+fQvy+jSABKLcaJPnoBGIpdXrDpPu3Pc1b0IAPAIB8A5zBYclEUgCmLteP6rR0wj30wjhAukwBxDmyYMMnrdJCqLNsEBIApCYrV6/lbQPM3AagRxoBOMnbSQAW3l67zG84fOpkoEkAUkAc8r4wzx0kGG3+7HVLcq1FWSMPMTzsxDj5y2IgIcQzWFPAqq0rTlj/K2kPBoMutG4lYaf6YQEojgWERD7B0l97v5u7nudDPuTiaSho5D7koz+30Mj9AQ0Hj4ic50EpAKYvcbvGulSCuwCANtnCNQ1FOH7XAeSClBq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" },
        { "soldat", "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" },
        { "agent", "iVBORw0KGgoAAAANSUhEUgAAAQAAAAEACAYAAABccqhmAAAd9UlEQVR42u3de3DdZ3kn8O/zvL/fObodXePElmRZVm6sCGHbACHDMt4NpEnKdrbZAltYOplMCsvQZRdo6Q7s0MIQpt3dXrfsUi5tpnTptgRzb0sHAusNXSBcE0KM77J9JGM7tu7Sufze99k/fr9zfCRLtmTLtiS+n5mDSCxH9jm/93mf93lvABERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERXWvCt4CfewPjW0O0uRq6AnDArij9Cr3A9zd8b/372UkwA6AN2OgFQLLUN/T39zerapOIGACYmURRFA4fPjy5zH/TZV8DswQGAFrfDd83NtwdO26+xXv/PAC3m9kLRaQTwI0ACtbQlEXMm8mzAEqq8iRgh8zsR953/+jEie/OLQoGlgUDYgCga6zW29cavhsYuPFOM3vADK8CcIuquvRbDGmjr31tDADp41D7mn6PwQzHAHxVBJ+ZnZX/c/bswakLBBxiAKCr99ntcsCeBAD6+m7qB/yviuhrRfBPRCRrwIa0kZrVmnn2uS/12We9uln666KSyv5tKJrh783sg6OjR55mRsAAQNeGq/W8vb2Dtzrn/pOZ/ZKqtmeN3gDzgOgFGvtKBcBCFgxURBCCeQBfDSH817GxI19Z/GciBgC6YnZFwJ6kq2uoo6VF3i6Ct6lqRwgeWaOXi1T6L4dlwcCpai27eNT78LtjY0f2Zz/XwEIhAwBdkc9KAIT+/sF7AfffVfWWEDzMzIvUe/uLTu4vlRLYMt93AR6AqDoNIUwCeE+xeOhPmA0wABCuSKEvAEB//9Bvi+h70yJdSABZcq7eGit1YnBq0Oz/iwDZDOCC3xBMECAwA7wJQpD6wH7ZlUNmiYhEqg4h+E+GUHrL2NjYGQARlpmGJAYAWuV4f2BgoMss+t8i7t4QfK1daq1qXyv4GQARQaRArAEqljb6FSz3W/ww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Sa/jCqcA+2Cx2PRfgGcr17gxLQg6/f39zc41DwJha3pFXpjJ5WT/4cOHJ5f7PQwAtGGzAWA4t317+X4z+zVA7lZVZ1a7Scd8w4q8xcP+5RKDcO7uq1qjl1pa/XQI+GC1Gj5/6tSRk+uoMUnDe+KXf892abbi0vgIMQBsqmwAAAYGbhoOwX4esAcA3KlaS4ORHchR/ydb+gh/yW7YReM9fHtF8Lci8pljxw59q+HnrdcVcksFusBGzwCwmT9HXfyQDwzcNGwWXmImLwRwOxBuBqQNkC5ZogoYgk2KWEVEngXkMGBPmblvFIsHvruwV32NAx5jg2IAoPU5NFg6zd22bVtLHMf5ENxNzmlb461YIt57n3u2uTmpLhoro6HYZuxJiTZWMIjS16q2fmua3u+KlltwRMwAaON+3nqRSy/YwxMREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREV0l/x9u/sJADBKKrgAAAABJRU5ErkJggg==" },
        { "survivante", "iVBORw0KGgoAAAANSUhEUgAAAQAAAAEACAYAAABccqhmAAAiuElEQVR42u3de5Bc2V0f8O/vd+7tnp73jKRdaaZnpBntem1hr8HrB4YYGYLjB384TrCLRxGXC0x4hAQICYEkYApSlaSKpBJIQcUQJ4EEbMeFCSRZysZYZcAY74Ifa+1L0ujRM9rRSvN+dPe95/zyx723p+clzWgk7Wj2+6nqmtrVPLv7/M45v/M75wBERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERPRSEz4FfN3bGJ8aooPV0BWAA05H2UfoTT6/7XNbn89OgiMAug8bvQBIt/qEarVaUdUOETEAMDOJoihcuHBhfpvv6fKPgaMEBgDa3w3ftzfc48cffoX3/pUAHjWz14pIP4CTAHqsrSmLmDeTswDqqvKXgJ03s695P/i1q1efXNkQDCwPBsQAQC+xorcvGr4bHT35JjN7jxm+A8ArVNVln2LIGn3xsT0AZG+H4mP2OQYzXAbwGRH83vKyfHZm5tzCTQIOMQDQvXvtTjvgTAoAw8MPVQH/AyL6PhG8SkTyBmzIGqlZ0czz132r1z7v1c2yfxeVTP5/Q80M/8/MfnVycuIrHBEwANBLwxU979DQiUeccz9tZn9XVXvzRm+AeUD0Jo19pwJgIQ8GKiIIwTyAz4QQ/u3U1MSnN/5OxABAd83pCDiTDgyM93V2yk+I4MdVtS8Ej7zRy8ZMv2yx1mfbzCVusSZoeTBwqlqMLj7iffjXU1MTz+XfYrtvTwwAtMfXSgCEavXE2wH3H1X1FSF4mJkXafX2615YA+ANrTm/CBAJEOn6VhoMSELeeg1QyR/bBwMPQFSdhhDmAfzLWu38r3A0wABAuCuJvgAA1er4z4voh7IkXUgBWbdWL5I14KLRRwr0xIbe2FCJgM7IUFKg7GxdUEgDUPeCuhespMBSIlhIBI18Zh9JkRrcMCQwS0UkUnUIwX8shPqPTE1N3QAQ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WzZ8BMAsS6KF5723D1y9euHPsjn/k8ndvGMEQKhWx39eRD+UrxT4Dft/pC0JmG/IMoioy+8T+BjQ/OFarTZz0Nb3GQAO6HQAAIaHxz4kov9cVaKwdpaVbrrlFGu7kpA34EiActuZBa2xu2W3FfuQBQLLF/BU1r7x5iy/eRGN8h1+v+v96j+Ympq6cY8q5dqCwIm3A/rLqvp17fcGtDVoyRU7C68B+KVa7fyv3Gq3HAMAYR8WboXh4RNvEnG/qCpvy47Ca/V+unFqgA0Xj5ptyCa2nV0gW3wNtujxs15UYGZPmdm/qtUu/O7GQHVvnI6AM+mpU6dKi4uNd4aA94vgMcCGRbLrfbNgEF4E9Ctm+Gia2u9PT1+4lj9XL7thPwPAARoNVKvj3y0iPysir84DAbIKQpGNweBWL7rddFXCQp7gk2z4HK4B9qu1Wse/Ac42X+LGtC7oVKvVinOVE0A4ml2RF5ZKJXnuwoUL89t9DQMA3bejAeBUaWSk8U4z+1FAvi07dyDkPZ/5toq89sfNYkBYmyAUjV6KYfVXQsCvJkn439euTUzvo8Ykbc+J3/45O615xSWvBmIAOFijAQAYHX3oVAj2LsDeA+BNqsUwuDifoPVftvUZHpLfsIv2e/ieFsH/EZHfu3z5/BfW74TclxVyWwW6wEbPAHCQX0fd+CYfHX3olFl4o5m8FsCjQHgYkG5ABjafw2cIweZFrCkiZwG5ANiXzdzna7Xnn1zfq77X5ReoskExAND+mxpsPcw9duxYZxzH5RDcQ85pd/utWMXhJZVKmmyYK6Mt2WbsSYnur2AQZY9dbf3W/KCTaGPBEXEEQPf/6623uPSCPTwREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREdE98v8BoaNESbYRkQwAAAAASUVORK5CYII=" },
        { "casquette", "iVBORw0KGgoAAAANSUhEUgAAAQAAAAEACAYAAABccqhmAAAhXklEQVR42u3de3TdV3Un8O/e53ef0pV8bccvyfIjL6KE8AgkJV2MCa+EyZrp4hGmBQamQ2EobQnPKW1XQ1iFaTuTMizKo5CuxcwUyiOTtkBmgGFoYygrK02YkpA4OLZlx7mSYzu2rPd9/M7Z88fvd+UrWZIlWZYl5/tZ6y6tOJJ+uo+zzz77nN85ABERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERXWjCl4DvcwvjS0W0uhu6AnDArij5Cl3Az7f87OTPyzL+7eyQmAHQIhu9AIhn+obu7u6CquZFxMxsynve/LcoikJfX9/QLNdw6dewDFlC+lx2CbA7pNckBgCapeH71oa6bdvlV3jvnwfgWjN7gYisAXApgJLN0XRFzJvJHgBVVfknwA6Y2ePer338yJGfjk8LBrbEDVO2bdvW6ZyzGYLQ+bgeMQCsWs3evtnwXU/PpTeY2evNcCuAK1TVJd9iSBp98+tcASD5ODS/Jj9jMMNhAH8vgr8dG5P7T57cPzxHAFrM58+2bdu2xnv3MwDtAH4uIgfN7Mdm+oP+/v2VlkAQWKdgAHgOv1e7HLA7BoCursu6Af8bIvpmEVwlImmDNSSN0qzZrNP2LfOo+KW9rFlyPVFJpP8aKmb4rpl9tr//4KNL0EMLAOvp6Sl7Hz2lqqXTwQgIIQyLyL1m9qlKpe+xlgDIbIAB4DnFNXvaLVu2X+mc+10ze6OqdqSN3gDzgKikjV2SThyW/qC302+2AVABopboYOn3zhwULKTBQEUEIZgH8PchhP88MHDw/07/GxcZAPaLSDl5Hpb+P3GqihBC3QxfHB/HHw4O9g0t8lrEALAa7YqA3XG5vLOzWJQPiOD9qtoZgkfa6AWANqt0DQA1MzTM0hK+oF0F7aqT3aam33MqBJgBDRgyEOREkEkjSLAZg4GlwcCpajPb+LL34U8G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K5WDf4VkZ5vZrtV6Fqmf/TXcpekKTB4NxACwurMBAOjpuaw3BPuXgL0ewA2qzbQ3LX/Z5H/N5wOf1s1kMo1Ix9NPiOB/icjfHj584MGW65/rCrl0DN+91ixzRNVlzcIggCMi8iCAb5VKue/u2bOnvsCxuuDMTZADGz0DwMX0vun0D3VPz2W9ZuF6M3kBgGuBcDkg7YCUz35QhCEEGxKxuojsAaQPsEfM3AOVyr6fTu1Vb3PAPUvWoHbt2hUdOPDUjQAkhPxjAwO/ODWtF3dswAwAtMC0dvPmzcVMJpMLwV3mnLbPdQqWiPfeZ/cUCnFj2li52UwjYLctY0N07LmJFh4MouRxTosC02UCu6KWfeWWo8E7LM/5JcQM4Dn1/upCzi5gj0tERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERLR8/j9vnQplVYQnFgAAAABJRU5ErkJggg==" },
        { "ninja", "iVBORw0KGgoAAAANSUhEUgAAAQAAAAEACAYAAABccqhmAAAgRElEQVR42u3de5RdV30f8O/vt8+5j5m589DL1syVRhoZwxK2U9fLxA4rUXgak9U0dEFrSgg4jru6VkugFEygkJYVs7J4JEBDSXEIXoVACcYtjyxoCy5VCBjbOMFGCGzJI1maGWELaaR53sfZ+9c/zjl37oxG0sxYjxnp+1nrLtnyeObOuWf/zt6//dt7A0RERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERLRKCC8BP/c2xktDdGk1dAXggF1R9s96hq/P/vuuKPt6x4cEewC0Nhs9APjFvuCmarU8rVpSEQOAYCbTURSGh4dPnuZ7OmCXALsDgMBLzABAq48CuxTYnbQ33K7B511d8P4Fscg/RrAXiVgM4Cox6ZrX3xfzZvITEfMGfdyJPaZme71ft+fIkUdn5geD0wcXYgCgC2teg1y37qruzk57STD8lsJ+uUNwtYo6iABm2UDfFh3tiwgMgIpg1gxNM8DskIg8amZfN9NvjY7uH1nQ02AgYAC4ZK+Lre4nPiRvgNXq0DUieJMZblPVqgfQBcNHSjG6xXwCmGa/lwFii3z2CoQAoENgfziTyN970y4VMUm/NIQwAeCrIva5w4cPfIuBYO2LeAlOeZpa2zh3td7cLn9P/f3bX64qbwHwKhFXAAIshBCAAIFWBFIRccnSor3LAgAikbSvYBaCWdZlkG5VfaOZvbFaHXoQsE+PjBz4TPZeXHbdOJPAALBWx9BpoxoaGupJkkQPHTo0nv2dZK+wCnomAsD392+/2jn9AwC3iwhCCAghSQBx0pbtT9peS+nuhexrw1w71rZOkYXgAyAqojeLyM3V6o5/GYL98djY8ANtX89E4Rq66Sm7afv7h15WrQ59tdHAPrNof7U69L1qdfvvtvUKoos4bGr1TqrVHW9R1YdF9HYzCyF4n/XsW+/PzABYK2Is9+XUpTkBs+x7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pFwsII4cfnFiEvuePoLhw8/gF+MTiOMIHeViujYhfZ+JalTIhif//OTJkw1gr12uJbCXrYmJ8a9XKj1eBM8X0Z60ZdkEgC+EMHvb6Oih7x8/fvxCN/x5w5U0CPRARF8qAknzAumQIF9N6H2SZcABlXTf+sI5CgBoO7ew2Wyg0ajD+7zyTuadgKwauRD8l4DG6w8fPjx+HufNF1wXEcC8ZbutRpFDuViQUiFGHLvsMCHLk7iq6ha+T8FluC0z90XOPvj1659fKZfr1zvnnPf65MhIPvWzKwJ2X8wkUN7KQrW67RbA3auqm7NFSdKeu8gDgNMIEsXocYqPumSFQ4AYf2+KsgUkIcD7JpIkWWyaLRsuucjMEjN75+jo8MfOtgrt3F8X/RNVfWH7uQFtPzsrQZB8RuBZAHePjDz1ZxfgfTIArJWcwCqf+okAJFu2bOkHCn8iIrelN3pITileMkMCoEcVn+yI0S3LCwAdAry3ATzcDCiZR7Cw2MGTbXkShVl4KAT/1tHRgw9lDeoCJdB2RcDuZOfOnYXJyfqtIeBNIrgBsAGR9HjfNBiEo4A+boa/ThL76jPPDD97Yd8nA8BauBa6oGR11QaqanXoNhH5DyJyTfbEywOYy5OA3QJ8sqOwogDwrukmHkkCulQWXoj8VA2n6hBCeBawPxsZGf4g0o0nLkad/LyfWa1Wy86VtwHhyvSIvDBVKMiTw8PDJ88S9BkAaE18Zlmeb2ehWq29SwT/SkSr2YYbJjCfQLRbIJ/oKMhKAsB7ZhI8kgR0ChDm6uKzMllBCGFCRO4HGu89fPjw2CroSrcHcH/6upddepGHdAwAdG6femn+ovE6QH5PRG5WETTN0A3DZzoKvkdgzfkNRBb95A0WAHQJwr+bSezBpkeXirP54+efAfZFM/3LbGVcPjxZTY1K2l4L918kBoBLbtji50pld/yKc/aaYPiNCLj6BXHkorl579aSt8Uag0DUBHAiGA7ApAGSjv8PieABAF8pFuV/79+/v36pl8gyANBaDARtjXFXdOXW0auPJeFFRbVfcibXO9gLAXEi6DtlwYwBwewkgBCAfWWVn0Swx83s+97X9hw5cmTmcqiNZwCgS2FokOcB533QLxsa6qkliT4b3FXitKv9WDwV77t9YW9XoZ589+mnT4TT14yw4ROtnV7BrihbgKPL/H8d5hbu8EHBHgBdQp+3nuXUGz7hiYiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiC6s/w8V78NZ173QjgAAAABJRU5ErkJggg==" },
        { "robot", "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d16H63ZpO17tYKBdesjnivmJF6LrvWz4UC6X89bm9wJ+Z5IAIn4xk5ETY2Njc9f6HgYA6qIgsGaXdoOpxu38uGWB5DppyR3nS4nZ3PaGF6zr3dljcteeczligIfcy/mpzwDQ1dbq0m4sQViunKjdPImo/TtdfZ+MNE/Y7fy+G6/h9yJ1vVcLdJ6NngFgS80JXKtLu94M/WYA2APIXQDuBPwrAOkBZODqXAKF9zonopGIHFOVcVV9AsCTraRfbGgpEHCOXW8GAHo+u7QbsmvXrkIYhlnv7UFrTU9nMBFxzrnMsXw+iVc0WNyc5U52vYk2GwxupCZckA4rNrT1u/mzjgQ3qSYdsQdAXXS9zRqHm/ApTURERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERE9AL5/xLjt4+YE5+zAAAAAElFTkSuQmCC" },
    };
}
