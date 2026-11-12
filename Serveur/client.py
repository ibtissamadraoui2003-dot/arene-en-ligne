"""
CLIENT DU JEU : jeu + panneau réseau (TCP + Pygame)
==================================================
À gauche : le jeu. Déplace ton carré avec les flèches et ramasse les pièces.
           Les autres joueurs apparaissent en temps réel.
À droite : ce qui se passe sur le réseau :
  - les paquets qui partent vers le serveur et ceux qui en arrivent (animation) ;
  - le ping (temps d'aller-retour) mesuré chaque seconde, avec un graphique ;
  - les paquets et octets envoyés/reçus, le débit ;
  - le journal de tous les messages.

Touches :  Flèches = bouger   Entrée = écrire dans le chat   F = afficher/masquer les paquets fréquents
           R = se reconnecter si la connexion est perdue      Échap = quitter

Lancement :
  python client.py                 -> serveur sur la même machine
  python client.py 192.168.1.20    -> serveur sur une autre machine du réseau
"""

import socket
import threading
import queue
import sys
import time
from collections import deque
from datetime import datetime

import pygame

from commun import (PORT, TERRAIN_L, TERRAIN_H, TAILLE, RAYON_PIECE, TICK_RATE,
                    COULEURS_TYPES, FREQUENTS, FOND, FOND_PANNEAU, BORDURE, TEXTE, TEXTE_GRIS,
                    encoder, LecteurMessages, resumer, Paquet, charger_polices, ecrire)

HOTE = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"

LARGEUR, HAUTEUR = 1160, 640
ZONE = pygame.Rect(12, 54, TERRAIN_L, TERRAIN_H)   # le terrain de jeu dans la fenêtre
PX = 726                                           # début du panneau réseau
VITESSE = 5
POS_CLIENT = (PX + 60, 116)                        # mini-diagramme client <-> serveur
POS_SERVEUR = (PX + 362, 116)


# ============================================================================
# Partie réseau (tourne dans un thread séparé)
# ============================================================================
class Connexion:
    """
    Gère la socket TCP. La connexion et la réception se font dans un thread,
    parce que connect() et recv() sont BLOQUANTS : dans la boucle de jeu,
    ils figeraient la fenêtre.
    """

    def __init__(self, hote, port):
        self.hote, self.port = hote, port
        self.etat = "connexion"        # connexion -> connecte -> perdue / echec / fermee
        self.sock = None
        self.boite = queue.Queue()     # thread réseau -> boucle de jeu
        self.duree_handshake_ms = None
        self.adresse_locale = None
        self.envoyes = self.recus = 0
        self.octets_envoyes = self.octets_recus = 0
        threading.Thread(target=self._executer, daemon=True).start()

    def _executer(self):
        # 1) Connexion : connect() déclenche la poignée de main TCP (SYN / SYN-ACK / ACK)
        try:
            self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            self.sock.settimeout(5)
            t0 = time.perf_counter()
            self.sock.connect((self.hote, self.port))
            self.duree_handshake_ms = (time.perf_counter() - t0) * 1000
            self.sock.settimeout(None)
            self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            self.adresse_locale = self.sock.getsockname()
            self.etat = "connecte"
            self.boite.put(("connecte", None, 0))
        except OSError as e:
            self.etat = "echec"
            self.boite.put(("echec", getattr(e, "strerror", None) or str(e), 0))
            return

        # 2) Réception en boucle
        lecteur = LecteurMessages()
        raison = "serveur fermé (FIN)"
        try:
            while True:
                donnees = self.sock.recv(4096)
                if not donnees:
                    break
                for message, taille in lecteur.ajouter(donnees):
                    self.recus += 1
                    self.octets_recus += taille
                    self.boite.put(("message", message, taille))
        except ConnectionResetError:
            raison = "coupure brutale (RST)"
        except OSError as e:
            raison = f"interrompue ({e.__class__.__name__})"
        except (ValueError, UnicodeDecodeError):
            raison = "message invalide reçu"
        if self.etat == "connecte":
            self.etat = "perdue"
            self.boite.put(("perdue", raison, 0))

    def envoyer(self, message):
        """Envoie un message au serveur. Renvoie le nombre d'octets envoyés (0 si échec)."""
        if self.etat != "connecte":
            return 0
        donnees = encoder(message)
        try:
            self.sock.sendall(donnees)
        except OSError:
            return 0
        self.envoyes += 1
        self.octets_envoyes += len(donnees)
        return len(donnees)

    def fermer(self):
        self.etat = "fermee"
        if self.sock is not None:
            try:
                self.sock.shutdown(socket.SHUT_RDWR)   # envoie le FIN au serveur
            except OSError:
                pass
            self.sock.close()


# ============================================================================
# Le jeu
# ============================================================================
class Jeu:
    def __init__(self, hote):
        self.hote = hote
        self.journal = deque(maxlen=300)
        self.tout_afficher = False
        self.nouvelle_connexion()

    def nouvelle_connexion(self):
        self.reseau = Connexion(self.hote, PORT)
        self.mon_id = None
        self.mon_nom = "?"
        self.ma_couleur = [255, 255, 255]
        self.x = self.y = 0
        self.joueurs = {}              # id -> infos (position reçue + position affichée)
        self.piece = None
        self.paquets = []
        self.pings = deque(maxlen=60)
        self.ping_ms = None
        self.bulles = {}               # id -> (texte, heure) : bulles de chat
        self.notifications = []        # (texte, heure)
        self.saisie = None             # texte du chat en cours d'écriture (None = pas en train d'écrire)
        self.dernier_envoi_pos = 0.0
        self.derniere_pos_envoyee = None
        self.dernier_ping = 0.0
        self.debit = {"envoyes": 0, "recus": 0, "o_envoyes": 0, "o_recus": 0}
        self._precedent = (0, 0, 0, 0)
        self._t_debit = time.time()
        self.journaliser("**", f"Connexion à {self.hote}:{PORT}...", "info")

    # ------------------------------------------------------------------ outils
    def journaliser(self, sens, texte, type_msg, octets=None):
        if type_msg in FREQUENTS and not self.tout_afficher:
            return
        heure = datetime.now().strftime("%H:%M:%S.%f")[:-3]
        ligne = f"{heure} {sens} {texte}"
        if octets:
            ligne += f" ({octets} o)"
        self.journal.append((ligne, COULEURS_TYPES.get(type_msg, TEXTE)))

    def envoyer(self, message):
        octets = self.reseau.envoyer(message)
        if octets:
            type_msg = message["type"]
            self.journaliser("->", resumer(message), type_msg, octets)
            etiquette = None if type_msg in FREQUENTS else type_msg
            self.paquets.append(Paquet(POS_CLIENT, POS_SERVEUR, type_msg, duree=0.4, etiquette=etiquette))

    # ------------------------------------------------------- messages du serveur
    def traiter_reseau(self):
        while True:
            try:
                genre, contenu, taille = self.reseau.boite.get_nowait()
            except queue.Empty:
                break
            if genre == "connecte":
                d = self.reseau.duree_handshake_ms
                ip, port = self.reseau.adresse_locale
                self.journaliser("**", f"Poignée de main TCP réussie en {d:.1f} ms", "ACK")
                self.journaliser("**", f"Mon adresse : {ip}:{port} (port choisi par l'OS)", "info")
                self.journaliser("**", "En attente du message 'bienvenue'...", "info")
                for i, (t, a, b) in enumerate([("SYN", POS_CLIENT, POS_SERVEUR),
                                               ("SYN-ACK", POS_SERVEUR, POS_CLIENT),
                                               ("ACK", POS_CLIENT, POS_SERVEUR)]):
                    self.paquets.append(Paquet(a, b, t, duree=0.45, delai=i * 0.45, etiquette=t))
            elif genre == "echec":
                self.journaliser("**", f"Échec connexion : {contenu}", "erreur")
            elif genre == "perdue":
                self.journaliser("**", f"Déconnecté : {contenu}", "FIN")
                self.paquets.append(Paquet(POS_SERVEUR, POS_CLIENT, "FIN", duree=0.5, etiquette="FIN"))
            elif genre == "message":
                self.recevoir(contenu, taille)

    def recevoir(self, message, taille):
        type_msg = message.get("type", "?")
        self.journaliser("<-", resumer(message), type_msg, taille)
        etiquette = None if type_msg in FREQUENTS else type_msg
        self.paquets.append(Paquet(POS_SERVEUR, POS_CLIENT, type_msg, duree=0.4, etiquette=etiquette))

        if type_msg == "bienvenue":
            self.mon_id = message["id"]
            self.mon_nom = message["nom"]
            self.ma_couleur = message["couleur"]
            self.x, self.y = message["x"], message["y"]
            self.journaliser("**", f"Je suis le joueur {self.mon_nom}", "bienvenue")
            pygame.display.set_caption(f"Client - {self.mon_nom} - serveur {self.hote}:{PORT}")

        elif type_msg == "etat":
            recus = message["joueurs"]
            for cle, infos in recus.items():
                id_joueur = int(cle)
                if id_joueur not in self.joueurs:
                    self.joueurs[id_joueur] = dict(infos, ax=float(infos["x"]), ay=float(infos["y"]))
                else:
                    self.joueurs[id_joueur].update(infos)
            for id_joueur in list(self.joueurs):
                if str(id_joueur) not in recus:
                    del self.joueurs[id_joueur]
            self.piece = message.get("piece")

        elif type_msg == "pong":
            # ping = temps d'aller-retour client -> serveur -> client
            self.ping_ms = (time.perf_counter() - message["t"]) * 1000
            self.pings.append(self.ping_ms)

        elif type_msg == "chat":
            self.bulles[message["id"]] = (message["texte"], time.time())

        elif type_msg == "evenement":
            self.notifications.append((message["texte"], time.time()))

    # ------------------------------------------------------------ mise à jour
    def mettre_a_jour(self, dt):
        connecte = self.reseau.etat == "connecte" and self.mon_id is not None
        maintenant = time.time()

        if connecte and self.saisie is None:
            touches = pygame.key.get_pressed()
            if touches[pygame.K_LEFT]:
                self.x -= VITESSE
            if touches[pygame.K_RIGHT]:
                self.x += VITESSE
            if touches[pygame.K_UP]:
                self.y -= VITESSE
            if touches[pygame.K_DOWN]:
                self.y += VITESSE
            self.x = max(0, min(TERRAIN_L - TAILLE, self.x))
            self.y = max(0, min(TERRAIN_H - TAILLE, self.y))

        if connecte:
            # position envoyée au maximum 20 fois/s, et seulement si on a bougé
            if ((self.x, self.y) != self.derniere_pos_envoyee
                    and maintenant - self.dernier_envoi_pos >= 1 / TICK_RATE):
                self.envoyer({"type": "deplacement", "x": self.x, "y": self.y})
                self.derniere_pos_envoyee = (self.x, self.y)
                self.dernier_envoi_pos = maintenant
            # ping toutes les secondes (on envoie l'heure, le serveur la renvoie)
            if maintenant - self.dernier_ping >= 1:
                self.dernier_ping = maintenant
                rtt = round(self.ping_ms) if self.ping_ms is not None else None
                self.envoyer({"type": "ping", "t": time.perf_counter(), "rtt": rtt})

        # interpolation : les autres joueurs glissent vers leur position reçue (plus fluide)
        f = min(1.0, dt * 15)
        for j in self.joueurs.values():
            j["ax"] += (j["x"] - j["ax"]) * f
            j["ay"] += (j["y"] - j["ay"]) * f

        # débit par seconde
        if maintenant - self._t_debit >= 1:
            r = self.reseau
            actuel = (r.envoyes, r.recus, r.octets_envoyes, r.octets_recus)
            ecoule = maintenant - self._t_debit
            for i, cle in enumerate(["envoyes", "recus", "o_envoyes", "o_recus"]):
                self.debit[cle] = (actuel[i] - self._precedent[i]) / ecoule
            self._precedent = actuel
            self._t_debit = maintenant

        self.paquets = [pq for pq in self.paquets if not pq.fini()][-300:]
        self.notifications = [(t, h) for t, h in self.notifications if maintenant - h < 4][-4:]

    # ================================================================ affichage
    def dessiner(self, ecran, p):
        ecran.fill(FOND)
        self.dessiner_barre(ecran, p)
        self.dessiner_terrain(ecran, p)
        self.dessiner_bas(ecran, p)
        self.dessiner_panneau(ecran, p)

    def dessiner_barre(self, ecran, p):
        pygame.draw.rect(ecran, FOND_PANNEAU, (0, 0, LARGEUR, 44))
        pygame.draw.line(ecran, BORDURE, (0, 44), (LARGEUR, 44))
        ecrire(ecran, p["titre"], "CLIENT", (14, 9))
        etat = self.reseau.etat
        if etat == "connecte" and self.mon_id is not None:
            couleur, texte = (80, 220, 120), f"Connecté à {self.hote}:{PORT} en tant que {self.mon_nom}"
        elif etat in ("connexion", "connecte"):
            couleur, texte = (255, 190, 60), f"Connexion à {self.hote}:{PORT}..."
        else:
            couleur, texte = (255, 80, 80), "Déconnecté   (R pour se reconnecter)"
        pygame.draw.circle(ecran, couleur, (120, 22), 7)
        ecrire(ecran, p["normal"], texte, (134, 13), couleur)
        ping = f"{self.ping_ms:.0f} ms" if self.ping_ms is not None else "-"
        ecrire(ecran, p["gras"], f"Ping : {ping}", (PX, 13), TEXTE)
        ecrire(ecran, p["normal"], f"{len(self.joueurs)} joueur(s) en ligne", (PX + 140, 13), TEXTE_GRIS)

    def dessiner_terrain(self, ecran, p):
        maintenant = time.time()
        pygame.draw.rect(ecran, (14, 16, 23), ZONE)
        for gx in range(ZONE.x, ZONE.right, 50):
            pygame.draw.line(ecran, (24, 27, 37), (gx, ZONE.y), (gx, ZONE.bottom))
        for gy in range(ZONE.y, ZONE.bottom, 50):
            pygame.draw.line(ecran, (24, 27, 37), (ZONE.x, gy), (ZONE.right, gy))
        pygame.draw.rect(ecran, BORDURE, ZONE, 2)

        if self.reseau.etat != "connecte" or self.mon_id is None:
            if self.reseau.etat in ("connexion", "connecte"):
                msg = "Connexion au serveur..."
            elif self.reseau.etat == "echec":
                msg = "Serveur injoignable. Lance serveur.py puis appuie sur R"
            else:
                msg = "Connexion au serveur perdue. Appuie sur R pour te reconnecter"
            ecrire(ecran, p["titre"], msg, ZONE.center, (255, 120, 100), centre=True)
            if self.reseau.etat not in ("perdue", "fermee"):
                return

        # pièce
        if self.piece:
            px, py = ZONE.x + self.piece[0], ZONE.y + self.piece[1]
            pygame.draw.circle(ecran, (255, 215, 80), (px, py), RAYON_PIECE)
            pygame.draw.circle(ecran, (200, 150, 30), (px, py), RAYON_PIECE, 2)

        # joueurs
        for id_joueur, j in self.joueurs.items():
            moi = id_joueur == self.mon_id
            # notre carré est affiché à notre position locale (pas d'attente du serveur)
            x, y = (self.x, self.y) if moi else (j["ax"], j["ay"])
            rect = pygame.Rect(ZONE.x + int(x), ZONE.y + int(y), TAILLE, TAILLE)
            pygame.draw.rect(ecran, j["couleur"], rect, border_radius=6)
            if moi:
                pygame.draw.rect(ecran, (255, 255, 255), rect, 2, border_radius=6)
            nom = f"{j['nom']} ({j['score']})" + ("  toi" if moi else "")
            ecrire(ecran, p["petit"], nom, (rect.centerx, rect.y - 10), TEXTE, centre=True)
            bulle = self.bulles.get(id_joueur)
            if bulle and maintenant - bulle[1] < 5:
                surface = p["normal"].render(bulle[0], True, (20, 20, 25))
                cadre = surface.get_rect(midbottom=(rect.centerx, rect.y - 22)).inflate(14, 8)
                pygame.draw.rect(ecran, (240, 240, 245), cadre, border_radius=8)
                ecran.blit(surface, surface.get_rect(center=cadre.center))

        # notifications (événements du serveur)
        for i, (texte, h) in enumerate(self.notifications):
            ecrire(ecran, p["normal"], texte, (ZONE.x + 12, ZONE.y + 10 + i * 20), COULEURS_TYPES["evenement"])

    def dessiner_bas(self, ecran, p):
        y = ZONE.bottom + 12
        if self.saisie is not None:
            cadre = pygame.Rect(ZONE.x, y - 4, ZONE.width, 28)
            pygame.draw.rect(ecran, (35, 38, 52), cadre, border_radius=6)
            pygame.draw.rect(ecran, COULEURS_TYPES["chat"], cadre, 1, border_radius=6)
            curseur = "_" if int(time.time() * 2) % 2 else " "
            ecrire(ecran, p["normal"], f"Message : {self.saisie}{curseur}", (ZONE.x + 10, y + 1))
        else:
            ecrire(ecran, p["normal"], "Flèches : bouger   |   Entrée : chat   |   F : filtre journal   |   "
                   "R : reconnexion   |   Échap : quitter", (ZONE.x, y), TEXTE_GRIS)

    def dessiner_panneau(self, ecran, p):
        r = self.reseau
        pygame.draw.rect(ecran, FOND_PANNEAU, (PX - 10, 54, LARGEUR - PX, HAUTEUR - 64), border_radius=8)

        # --- mini-diagramme client <-> serveur ---
        ecrire(ecran, p["gras"], "Échanges client <-> serveur", (PX, 60), TEXTE_GRIS)
        pygame.draw.line(ecran, (70, 75, 100), POS_CLIENT, POS_SERVEUR, 2)
        for pq in self.paquets:
            pq.dessiner(ecran, p["petit"])
        for pos, titre, couleur in [(POS_CLIENT, "MOI", self.ma_couleur), (POS_SERVEUR, "SERVEUR", (120, 150, 230))]:
            cadre = pygame.Rect(0, 0, 96, 36)
            cadre.center = pos
            pygame.draw.rect(ecran, (40, 45, 65), cadre, border_radius=8)
            pygame.draw.rect(ecran, couleur, cadre, 2, border_radius=8)
            ecrire(ecran, p["gras"], titre, pos, TEXTE, centre=True)
        local = f"{r.adresse_locale[0]}:{r.adresse_locale[1]}" if r.adresse_locale else "-"
        ecrire(ecran, p["petit"], local, (POS_CLIENT[0], POS_CLIENT[1] + 30), TEXTE_GRIS, centre=True)
        ecrire(ecran, p["petit"], f"{self.hote}:{PORT}", (POS_SERVEUR[0], POS_SERVEUR[1] + 30), TEXTE_GRIS, centre=True)

        # --- ping ---
        y = 166
        ping = f"{self.ping_ms:.1f} ms" if self.ping_ms is not None else "-"
        ecrire(ecran, p["gras"], f"Ping (aller-retour) : {ping}", (PX, y), TEXTE)
        graphe = pygame.Rect(PX, y + 22, LARGEUR - PX - 16, 44)
        pygame.draw.rect(ecran, (14, 16, 23), graphe)
        pygame.draw.rect(ecran, BORDURE, graphe, 1)
        if len(self.pings) >= 2:
            maxi = max(50.0, max(self.pings) * 1.2)
            pas = graphe.width / (self.pings.maxlen - 1)
            points = [(graphe.x + i * pas, graphe.bottom - 2 - (v / maxi) * (graphe.height - 4))
                      for i, v in enumerate(self.pings)]
            pygame.draw.lines(ecran, COULEURS_TYPES["ping"], False, points, 2)
            ecrire(ecran, p["petit"], f"max {maxi:.0f} ms", (graphe.right - 70, graphe.y + 2), TEXTE_GRIS)

        # --- statistiques ---
        y = 240
        d = self.debit
        ecrire(ecran, p["normal"], f"Envoyés : {r.envoyes} paquets  ({d['envoyes']:.0f}/s, "
               f"{d['o_envoyes']:.0f} o/s)", (PX, y), COULEURS_TYPES["deplacement"])
        ecrire(ecran, p["normal"], f"Reçus : {r.recus} paquets  ({d['recus']:.0f}/s, "
               f"{d['o_recus'] / 1024:.1f} Ko/s)", (PX, y + 19), COULEURS_TYPES["etat"])
        hs = f"{r.duree_handshake_ms:.1f} ms" if r.duree_handshake_ms is not None else "-"
        ecrire(ecran, p["normal"], f"Poignée de main TCP : {hs}   |   Protocole : TCP", (PX, y + 38), TEXTE_GRIS)

        # --- joueurs ---
        y = 306
        pygame.draw.line(ecran, BORDURE, (PX, y - 6), (LARGEUR - 16, y - 6))
        ecrire(ecran, p["gras"], "Scores", (PX, y), TEXTE_GRIS)
        classement = sorted(self.joueurs.items(), key=lambda e: -e[1]["score"])
        for i, (id_joueur, j) in enumerate(classement[:4]):
            ly = y + 20 + i * 17
            pygame.draw.rect(ecran, j["couleur"], (PX, ly + 3, 10, 10))
            moi = "  (toi)" if id_joueur == self.mon_id else ""
            ecrire(ecran, p["normal"], f"{j['nom']}{moi}", (PX + 16, ly), TEXTE)
            ecrire(ecran, p["normal"], f"{j['score']} pièce(s)", (PX + 150, ly), TEXTE_GRIS)

        # --- journal ---
        y = 400
        pygame.draw.line(ecran, BORDURE, (PX, y - 6), (LARGEUR - 16, y - 6))
        filtre = "affichés" if self.tout_afficher else "masqués"
        ecrire(ecran, p["gras"], "Journal réseau", (PX, y), TEXTE_GRIS)
        ecrire(ecran, p["petit"], f"deplacement/etat : {filtre} [F]", (PX + 125, y + 3), TEXTE_GRIS)
        hauteur_ligne = 15
        nb = (HAUTEUR - 18 - (y + 22)) // hauteur_ligne
        for i, (texte, couleur) in enumerate(list(self.journal)[-nb:]):
            if len(texte) > 57:
                texte = texte[:56] + "…"
            ecrire(ecran, p["mono"], texte, (PX, y + 22 + i * hauteur_ligne), couleur)

    # ============================================================ boucle principale
    def executer(self):
        pygame.init()
        ecran = pygame.display.set_mode((LARGEUR, HAUTEUR))
        pygame.display.set_caption(f"Client - serveur {self.hote}:{PORT}")
        polices = charger_polices()
        horloge = pygame.time.Clock()

        en_cours = True
        while en_cours:
            dt = horloge.tick(60) / 1000
            for ev in pygame.event.get():
                if ev.type == pygame.QUIT:
                    en_cours = False
                elif ev.type == pygame.KEYDOWN:
                    if self.saisie is not None:              # en train d'écrire un message
                        if ev.key == pygame.K_RETURN:
                            if self.saisie.strip():
                                self.envoyer({"type": "chat", "texte": self.saisie.strip()})
                            self.saisie = None
                        elif ev.key == pygame.K_ESCAPE:
                            self.saisie = None
                        elif ev.key == pygame.K_BACKSPACE:
                            self.saisie = self.saisie[:-1]
                    elif ev.key == pygame.K_ESCAPE:
                        en_cours = False
                    elif ev.key == pygame.K_RETURN and self.reseau.etat == "connecte":
                        self.saisie = ""
                    elif ev.key == pygame.K_f:
                        self.tout_afficher = not self.tout_afficher
                    elif ev.key == pygame.K_r and self.reseau.etat in ("perdue", "echec"):
                        self.nouvelle_connexion()
                elif ev.type == pygame.TEXTINPUT and self.saisie is not None:
                    self.saisie = (self.saisie + ev.text)[:60]

            self.traiter_reseau()
            self.mettre_a_jour(dt)
            self.dessiner(ecran, polices)
            pygame.display.flip()

        self.reseau.fermer()   # déconnexion propre : le serveur reçoit un FIN
        pygame.quit()


if __name__ == "__main__":
    Jeu(HOTE).executer()