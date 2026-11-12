"""
SERVEUR DE L'ARÈNE DE TIR (TCP + Pygame)
========================================
Un vrai petit jeu multijoueur : chaque joueur se déplace dans une arène,
vise à la souris et tire. Les balles font des dégâts, un joueur à 0 PV est
éliminé puis réapparaît 3 secondes plus tard.

LE SERVEUR FAIT AUTORITÉ (comme dans les vrais jeux en ligne) :
  - les clients n'envoient PAS leur position, seulement leurs ENTRÉES
    (touches appuyées, angle de visée, tir) : message "entree" ;
  - le serveur simule le jeu 30 fois par seconde (tick de simulation) :
    déplacements, collisions avec les murs, balles, dégâts, éliminations ;
  - il envoie l'état du monde 20 fois par seconde à tout le monde ("etat") ;
  - un client ne peut donc pas tricher (se téléporter, tirer plus vite, etc.).

Le tableau de bord réseau (topologie, paquets, journal, latence simulée)
vient de serveur.py, qui sert de base.

MODE MATCH : le premier à 10 éliminations gagne la manche, puis une nouvelle manche commence.
MODES DE JEU (touche M) : toi contre les bots / entre amis / démo (les bots entre eux, on regarde).
Un client peut aussi choisir de seulement REGARDER (spectateur) depuis le menu du jeu.

Touches :  M = mode de jeu   B = ajouter un bot   N = retirer un bot   D = difficulté des bots
           K = limite d'éliminations   L = latence simulée
           F = afficher/masquer les paquets fréquents   C = vider le journal   Échap = quitter

Lancement :  python serveur_arene.py      puis Play dans Unity (ClientArene.cs)
"""

import math
import random
import time
from collections import deque

import pygame

from commun import COULEURS_TYPES, TEXTE_GRIS, BORDURE, ecrire
from serveur import Serveur

# ----------------------------------------------------------------------------
# Règles du jeu (envoyées au client dans le message "bienvenue")
# ----------------------------------------------------------------------------
ARENE_L, ARENE_H = 1200, 800
RAYON = 20                 # rayon d'un joueur
VITESSE = 260.0            # unités par seconde
TICK_SIM = 30              # simulations par seconde
DT = 1.0 / TICK_SIM
ENVOI_ETAT = 20            # envois de l'état par seconde
PV_MAX = 100
DEGATS = 25
CADENCE = 0.28             # secondes entre deux tirs
BALLE_VITESSE = 750.0
BALLE_VIE = 1.1            # secondes
REAPPARITION = 3.0         # secondes
LIMITES = [10, 5, 20, 0]   # éliminations pour gagner une manche (0 = sans limite) ; touche K
DUREE_FIN = 7.0            # secondes d'écran de fin de manche
SKINS = ["soldat", "agent", "survivante", "casquette", "ninja", "robot"]

# Niveaux des bots (touche D) :
#   imprecision = erreur de visée (radians), tir = probabilité de tirer à chaque tick,
#   portee = distance à partir de laquelle le bot attaque, mobilite = part des ticks où il bouge
NIVEAUX = {
    "facile":    {"imprecision": 0.32, "tir": 0.07, "portee": 420, "mobilite": 0.6},
    "moyen":     {"imprecision": 0.19, "tir": 0.15, "portee": 520, "mobilite": 0.85},
    "difficile": {"imprecision": 0.10, "tir": 0.30, "portee": 650, "mobilite": 1.0},
}
ORDRE_NIVEAUX = ["facile", "moyen", "difficile"]

# Modes de jeu (touche M) : (nom affiché, nombre de bots au lancement du mode)
MODES = {
    "bots": ("Toi contre les bots", 2),
    "amis": ("Entre amis (sans bots)", 0),
    "demo": ("Démo : les bots entre eux", 4),     # les humains regardent (spectateurs)
}
ORDRE_MODES = ["bots", "amis", "demo"]

# murs : [x, y, largeur, hauteur]
MURS = [
    [270, 150, 50, 220], [880, 430, 50, 220],
    [520, 370, 160, 50],
    [130, 590, 230, 45], [840, 165, 230, 45],
    [560, 90, 80, 45], [560, 665, 80, 45],
]


def borner(v, mini, maxi):
    return max(mini, min(maxi, v))


def collision(x, y, r=RAYON):
    """Un cercle (x, y, r) touche-t-il un mur ?"""
    for mx, my, mw, mh in MURS:
        px, py = borner(x, mx, mx + mw), borner(y, my, my + mh)
        if (x - px) ** 2 + (y - py) ** 2 < r * r:
            return True
    return False


def deplacer(x, y, dx, dy, dt):
    """
    Déplacement d'un joueur pendant dt secondes.
    MÊME CODE dans le client Unity (fonction Deplacer) : c'est ce qui permet
    au client de PRÉDIRE sa position sans attendre la réponse du serveur.
    """
    n = math.hypot(dx, dy)
    if n == 0:
        return x, y
    vx, vy = dx / n * VITESSE * dt, dy / n * VITESSE * dt
    nx = borner(x + vx, RAYON, ARENE_L - RAYON)
    if not collision(nx, y):
        x = nx
    ny = borner(y + vy, RAYON, ARENE_H - RAYON)
    if not collision(x, ny):
        y = ny
    return x, y


def point_dans_mur(x, y):
    return any(mx <= x <= mx + mw and my <= y <= my + mh for mx, my, mw, mh in MURS)


class Joueur:
    """Un joueur dans l'arène (humain connecté par le réseau, ou bot simulé par le serveur)."""

    def __init__(self, id_joueur, nom, couleur, bot=False):
        self.id, self.nom, self.couleur, self.bot = id_joueur, nom, couleur, bot
        self.skin = "robot" if bot else "soldat"
        self.x, self.y, self.angle = ARENE_L / 2, ARENE_H / 2, 0.0
        self.pv, self.score, self.morts = PV_MAX, 0, 0
        self.vivant, self.t_reapparition = True, 0.0
        self.prochain_tir = 0.0
        self.dernier_seq = 0                 # dernière entrée traitée (renvoyée au client)
        self.entrees = deque()               # entrées reçues, pas encore simulées
        self.client = None
        # pour les bots
        self.but, self.t_but, self.cible_angle = None, 0.0, 0.0


class Balle:
    def __init__(self, id_balle, proprio, x, y, angle):
        self.id, self.proprio = id_balle, proprio
        self.x, self.y = x, y
        self.vx, self.vy = math.cos(angle) * BALLE_VITESSE, math.sin(angle) * BALLE_VITESSE
        self.vie = BALLE_VIE


class ServeurArene(Serveur):
    def __init__(self):
        super().__init__()
        self.titre = "SERVEUR ARÈNE"
        self.texte_attente = "En attente de joueurs... appuie sur Play dans Unity (ou B pour un bot)"
        self.aide = ("M : mode   B/N : +/- bot   D : difficulté   K : limite   L : latence   F   C")
        self.legende = [("SYN / ACK", "SYN"), ("bienvenue", "bienvenue"), ("entree (inputs)", "entree"),
                        ("etat (monde)", "etat"), ("ping / pong", "ping"), ("chat", "chat"),
                        ("evenement", "evenement"), ("FIN / RST", "FIN")]
        self.joueurs = {}
        self.balles = []
        self.prochaine_balle = 1
        self.prochain_bot = 1
        self.t_sim = None
        self.dernier_envoi = 0.0
        # mode match
        self.limite = LIMITES[0]
        self.phase = "jeu"            # "jeu" ou "fin"
        self.t_fin = 0.0
        self.gagnant = ""
        self.manche = 1
        self.niveau = "facile"        # difficulté des bots
        self.mode = "bots"            # mode de jeu

    # ------------------------------------------------------------------ arrivées
    def accueillir(self, c):
        if not c.actif:
            return
        c.pret = True
        j = Joueur(c.id, c.nom, c.couleur)
        j.client = c
        c.joueur = j
        c.veut_regarder = False
        self.actualiser_role(c)
        self.envoyer(c, {
            "type": "bienvenue", "id": c.id, "nom": c.nom, "couleur": c.couleur,
            "x": round(j.x, 2), "y": round(j.y, 2),
            # les RÈGLES viennent du serveur : le client les utilise pour sa prédiction
            "arene": [ARENE_L, ARENE_H], "murs": MURS, "rayon": RAYON, "vitesse": VITESSE,
            "tick": TICK_SIM, "envoi": ENVOI_ETAT, "pv_max": PV_MAX, "skins": SKINS,
        })

    def quand_deconnexion(self, c):
        self.joueurs.pop(c.id, None)

    def est_spectateur(self, c):
        return getattr(c, "veut_regarder", False) or self.mode == "demo"

    def actualiser_role(self, c):
        """Met le client dans l'arène (joueur) ou en dehors (spectateur : il reçoit l'état mais ne joue pas)."""
        j = getattr(c, "joueur", None)
        if j is None or not c.actif:
            return
        if self.est_spectateur(c):
            self.joueurs.pop(c.id, None)
        elif c.id not in self.joueurs:
            j.score = j.morts = 0
            j.entrees.clear()
            self.placer(j)
            self.joueurs[c.id] = j

    def placer(self, j):
        """Point d'apparition : libre, et le plus loin possible des autres joueurs."""
        meilleur, meilleure_dist = (ARENE_L / 2, ARENE_H / 2), -1
        autres = [o for o in self.joueurs.values() if o is not j and o.vivant]
        for _ in range(40):
            x, y = random.uniform(60, ARENE_L - 60), random.uniform(60, ARENE_H - 60)
            if collision(x, y, RAYON + 8):
                continue
            d = min([math.hypot(x - o.x, y - o.y) for o in autres], default=9999)
            if d > meilleure_dist:
                meilleur, meilleure_dist = (x, y), d
        j.x, j.y = meilleur
        j.pv, j.vivant = PV_MAX, True

    # ---------------------------------------------------------- messages reçus
    def logique_message(self, c, type_msg, message):
        if type_msg == "entree":
            j = getattr(c, "joueur", None)
            if j is None or c.id not in self.joueurs:      # spectateur : on ignore
                return
            j.entrees.append((int(message["seq"]), int(borner(message["dx"], -1, 1)),
                              int(borner(message["dy"], -1, 1)), float(message["angle"]),
                              bool(message.get("tir"))))
            while len(j.entrees) > 12:        # anti-triche : pas plus de 12 entrées en réserve
                j.entrees.popleft()
        elif type_msg == "profil":
            # le joueur choisit son pseudo et son personnage (le serveur vérifie)
            j = getattr(c, "joueur", None)
            if j is None:
                return
            nom = "".join(ch for ch in str(message.get("nom", "")) if ch.isprintable()).strip()[:12]
            if nom:
                j.nom = c.nom = nom
            skin = str(message.get("skin", ""))
            j.skin = skin if skin in SKINS else "soldat"
            c.veut_regarder = bool(message.get("spectateur"))
            self.actualiser_role(c)
            texte = f"{j.nom} regarde la partie" if self.est_spectateur(c) else f"{j.nom} a rejoint l'arène"
            self.diffuser({"type": "evenement", "genre": "arrivee", "texte": texte})
        else:
            super().logique_message(c, type_msg, message)   # ping, chat

    # -------------------------------------------------------------- simulation
    def tick(self):
        maintenant = time.time()
        if self.t_sim is None or maintenant - self.t_sim > 0.5:
            self.t_sim = maintenant
        # pas de temps FIXE : la simulation avance toujours de DT (1/30 s)
        while maintenant - self.t_sim >= DT:
            self.t_sim += DT
            self.simuler(self.t_sim)
        if maintenant - self.dernier_envoi >= 1 / ENVOI_ETAT:
            self.dernier_envoi = maintenant
            self.envoyer_etat()

    def simuler(self, t):
        if self.phase == "fin":
            for j in self.joueurs.values():           # tout est figé, on confirme juste les entrées
                while j.entrees:
                    j.dernier_seq = j.entrees.popleft()[0]
            if t >= self.t_fin:
                self.nouvelle_manche()
            return
        for j in list(self.joueurs.values()):
            if not j.vivant:
                while j.entrees:                       # on confirme quand même les entrées
                    j.dernier_seq = j.entrees.popleft()[0]
                if t >= j.t_reapparition:
                    self.placer(j)
                continue
            if j.bot:
                self.appliquer(j, self.cerveau_bot(j, t), t)
            else:
                n = 0
                while j.entrees and n < 4:            # 1 entrée = 1 pas de simulation
                    self.appliquer(j, j.entrees.popleft(), t)
                    n += 1

        for b in list(self.balles):
            b.x += b.vx * DT
            b.y += b.vy * DT
            b.vie -= DT
            if b.vie <= 0 or not (0 <= b.x <= ARENE_L and 0 <= b.y <= ARENE_H) or point_dans_mur(b.x, b.y):
                self.balles.remove(b)
                continue
            for j in self.joueurs.values():
                if j.vivant and j.id != b.proprio and math.hypot(j.x - b.x, j.y - b.y) < RAYON + 4:
                    self.balles.remove(b)
                    self.touche_joueur(j, b.proprio, t)
                    break

    def appliquer(self, j, entree, t):
        seq, dx, dy, angle, tir = entree
        j.x, j.y = deplacer(j.x, j.y, dx, dy, DT)
        j.angle = angle
        j.dernier_seq = seq
        if tir and t >= j.prochain_tir:
            j.prochain_tir = t + CADENCE
            bx, by = j.x + math.cos(angle) * (RAYON + 6), j.y + math.sin(angle) * (RAYON + 6)
            if not point_dans_mur(bx, by):
                self.balles.append(Balle(self.prochaine_balle, j.id, bx, by, angle))
                self.prochaine_balle += 1

    def touche_joueur(self, victime, id_tireur, t):
        victime.pv -= DEGATS
        if victime.pv > 0:
            return
        victime.pv = 0
        victime.vivant = False
        victime.morts += 1
        victime.t_reapparition = t + REAPPARITION
        tireur = self.joueurs.get(id_tireur)
        nom_tireur = tireur.nom if tireur else "?"
        if tireur:
            tireur.score += 1
        self.diffuser({"type": "evenement", "genre": "elimination",
                       "texte": f"{nom_tireur} a éliminé {victime.nom}",
                       "tueur": id_tireur, "victime": victime.id,
                       "x": round(victime.x, 1), "y": round(victime.y, 1)})
        if tireur and self.limite and tireur.score >= self.limite:
            self.phase, self.t_fin, self.gagnant = "fin", t + DUREE_FIN, tireur.nom
            self.balles.clear()
            self.diffuser({"type": "evenement", "genre": "fin_manche", "gagnant": tireur.id,
                           "texte": f"{tireur.nom} remporte la manche {self.manche} !"})

    def changer_mode(self, mode):
        self.mode = mode
        nom, nb_bots = MODES[mode]
        bots = [j for j in self.joueurs.values() if j.bot]
        while len(bots) < nb_bots:
            self.ajouter_bot()
            bots = [j for j in self.joueurs.values() if j.bot]
        while len(bots) > nb_bots:
            self.retirer_bot()
            bots = [j for j in self.joueurs.values() if j.bot]
        for c in self.clients.values():
            if c.actif and c.pret:
                self.actualiser_role(c)
        self.journaliser("**", "", f"Mode de jeu : {nom}", "info")
        self.diffuser({"type": "evenement", "genre": "regle", "texte": f"Mode : {nom}"})
        self.phase = "jeu"
        self.balles.clear()
        self.nouvelle_manche()

    def nouvelle_manche(self):
        self.manche += 1
        self.phase, self.gagnant = "jeu", ""
        for j in self.joueurs.values():
            j.score = j.morts = 0
            j.entrees.clear()
        for j in self.joueurs.values():
            self.placer(j)
        limite = f"premier à {self.limite} éliminations" if self.limite else "sans limite"
        self.diffuser({"type": "evenement", "genre": "nouvelle_manche",
                       "texte": f"Manche {self.manche} : {limite} !"})

    def envoyer_etat(self):
        prets = [c for c in self.clients.values() if c.actif and c.pret]
        if not prets:
            return
        maintenant = time.time()
        etat = {
            "type": "etat",
            "joueurs": {str(j.id): {
                "nom": j.nom, "x": round(j.x, 2), "y": round(j.y, 2), "a": round(j.angle, 3),
                "pv": j.pv, "score": j.score, "morts": j.morts, "vivant": j.vivant,
                "r": round(max(0.0, j.t_reapparition - maintenant), 1) if not j.vivant else 0,
                "couleur": j.couleur, "seq": j.dernier_seq, "bot": j.bot, "skin": j.skin,
            } for j in self.joueurs.values()},
            "manche": {"numero": self.manche, "phase": self.phase, "limite": self.limite,
                       "gagnant": self.gagnant, "mode": self.mode, "niveau": self.niveau,
                       "reste": round(max(0.0, self.t_fin - self.t_sim), 1) if self.phase == "fin" else 0},
            "balles": [[b.id, round(b.x, 1), round(b.y, 1), round(b.vx, 1), round(b.vy, 1), b.proprio]
                       for b in self.balles],
        }
        for c in prets:
            self.envoyer(c, etat, journal=False)
        self.journaliser("->", "TOUS", self._resume_etat(etat), "etat")

    @staticmethod
    def _resume_etat(etat):
        return f"etat ({len(etat['joueurs'])} joueur(s), {len(etat['balles'])} balle(s))"

    # -------------------------------------------------------------------- bots
    def ajouter_bot(self):
        id_bot = 1000 + self.prochain_bot
        couleur = [170, 170, 185]
        j = Joueur(id_bot, f"BOT{self.prochain_bot}", couleur, bot=True)
        self.prochain_bot += 1
        self.placer(j)
        self.joueurs[id_bot] = j
        self.journaliser("**", "", f"{j.nom} ajouté (simulé par le serveur, sans connexion réseau)", "info")
        self.diffuser({"type": "evenement", "genre": "arrivee", "texte": f"{j.nom} a rejoint l'arène"})

    def retirer_bot(self):
        bots = [j for j in self.joueurs.values() if j.bot]
        if bots:
            j = bots[-1]
            del self.joueurs[j.id]
            self.diffuser({"type": "evenement", "genre": "depart", "texte": f"{j.nom} a quitté l'arène"})

    def cerveau_bot(self, j, t):
        """IA très simple : se promène, vise le joueur le plus proche avec une imprécision, tire."""
        if j.but is None or t > j.t_but or math.hypot(j.but[0] - j.x, j.but[1] - j.y) < 25:
            for _ in range(20):
                but = (random.uniform(60, ARENE_L - 60), random.uniform(60, ARENE_H - 60))
                if not collision(but[0], but[1], RAYON + 8):
                    break
            j.but, j.t_but = but, t + random.uniform(2, 4)
        n = NIVEAUX[self.niveau]
        ex, ey = j.but[0] - j.x, j.but[1] - j.y
        dx = 0 if abs(ex) < 12 else (1 if ex > 0 else -1)
        dy = 0 if abs(ey) < 12 else (1 if ey > 0 else -1)
        if random.random() > n["mobilite"]:          # bot plus lent en facile
            dx = dy = 0
        cibles = [o for o in self.joueurs.values() if o is not j and o.vivant]
        tir = False
        angle = math.atan2(dy, dx) if (dx or dy) else j.angle
        if cibles:
            c = min(cibles, key=lambda o: math.hypot(o.x - j.x, o.y - j.y))
            if math.hypot(c.x - j.x, c.y - j.y) < n["portee"]:
                angle = math.atan2(c.y - j.y, c.x - j.x) + random.gauss(0, n["imprecision"])
                tir = random.random() < n["tir"]
        return (0, dx, dy, angle, tir)

    # --------------------------------------------------------------- affichage
    def texte_barre(self):
        nb_bots = len([j for j in self.joueurs.values() if j.bot])
        lim = f"à {self.limite}" if self.limite else "sans limite"
        return f"mode : {self.mode}   |   {nb_bots} bot(s) {self.niveau}   |   manche {self.manche} ({lim})   |   sim {TICK_SIM}/s"

    def info_noeud(self, c):
        j = getattr(c, "joueur", None)
        ping = f"{c.ping_ms} ms" if c.ping_ms is not None else "..."
        if j is None:
            return f"ping {ping}"
        if c.id not in self.joueurs:
            return f"ping {ping}  |  spectateur"
        etat = f"{j.pv} PV" if j.vivant else "éliminé"
        return f"ping {ping}  |  {j.score} K / {j.morts} M  |  {etat}"

    def dessiner_carte(self, ecran, p):
        echelle = 175 / ARENE_L
        carte = pygame.Rect(452, 92, int(ARENE_L * echelle), int(ARENE_H * echelle))
        ecrire(ecran, p["petit"], "Arène (vue du serveur = la vérité)", (carte.x - 10, carte.y - 17), TEXTE_GRIS)
        pygame.draw.rect(ecran, (12, 14, 20), carte)
        pygame.draw.rect(ecran, BORDURE, carte, 1)
        for mx, my, mw, mh in MURS:
            pygame.draw.rect(ecran, (70, 76, 100), (carte.x + mx * echelle, carte.y + my * echelle,
                                                    max(2, mw * echelle), max(2, mh * echelle)))
        for b in self.balles:
            pygame.draw.circle(ecran, (255, 230, 120), (carte.x + b.x * echelle, carte.y + b.y * echelle), 1)
        for j in self.joueurs.values():
            if j.vivant:
                pos = (carte.x + j.x * echelle, carte.y + j.y * echelle)
                pygame.draw.circle(ecran, j.couleur, pos, 4)
                if j.bot:
                    pygame.draw.circle(ecran, (255, 255, 255), pos, 4, 1)
        # classement
        y = carte.bottom + 8
        for j in sorted(self.joueurs.values(), key=lambda o: -o.score)[:6]:
            pygame.draw.rect(ecran, j.couleur, (carte.x, y + 4, 8, 8))
            etat = f"{j.pv:>3} PV" if j.vivant else "  mort"
            ecrire(ecran, p["petit"], f"{j.nom:<5} {j.score} K / {j.morts} M  {etat}", (carte.x + 13, y),
                   COULEURS_TYPES["info"] if j.bot else (225, 228, 235))
            y += 15

    def touche(self, touche):
        if touche == pygame.K_b:
            self.ajouter_bot()
        elif touche == pygame.K_n:
            self.retirer_bot()
        elif touche == pygame.K_m:
            self.changer_mode(ORDRE_MODES[(ORDRE_MODES.index(self.mode) + 1) % len(ORDRE_MODES)])
        elif touche == pygame.K_d:
            self.niveau = ORDRE_NIVEAUX[(ORDRE_NIVEAUX.index(self.niveau) + 1) % len(ORDRE_NIVEAUX)]
            self.journaliser("**", "", f"Difficulté des bots : {self.niveau}", "info")
            self.diffuser({"type": "evenement", "genre": "regle", "texte": f"Bots : niveau {self.niveau}"})
        elif touche == pygame.K_k:
            self.limite = LIMITES[(LIMITES.index(self.limite) + 1) % len(LIMITES)]
            texte = f"premier à {self.limite} éliminations" if self.limite else "sans limite"
            self.journaliser("**", "", f"Limite de la manche : {texte}", "info")
            self.diffuser({"type": "evenement", "genre": "regle", "texte": f"Nouvelle règle : {texte}"})


if __name__ == "__main__":
    serveur = ServeurArene()
    for _ in range(MODES[serveur.mode][1]):     # bots du mode de départ
        serveur.ajouter_bot()
    serveur.executer()