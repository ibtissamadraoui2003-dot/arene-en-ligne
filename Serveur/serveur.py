"""
SERVEUR DE JEU : tableau de bord réseau (TCP + Pygame)
======================================================
La fenêtre du serveur montre en direct :
  - la topologie : le serveur au centre, les clients autour,
    et chaque paquet qui voyage (un point coloré par type de message) ;
  - l'animation de la poignée de main TCP (SYN / SYN-ACK / ACK) et de la fin (FIN) ;
  - un tableau des clients : adresse IP:port, durée, paquets, octets, ping, score ;
  - le journal de tous les messages échangés ;
  - les statistiques : paquets par seconde, débit ;
  - l'état du jeu tel que le serveur le connaît (c'est lui qui fait autorité).

Touches :  L = latence simulée (0/100/250/500 ms)   F = afficher/masquer les paquets fréquents
           C = vider le journal                     Échap = quitter

Architecture :
  - 1 thread qui accepte les connexions
  - 2 threads par client : un pour RECEVOIR, un pour ENVOYER
  - le thread principal (boucle Pygame) applique la logique du jeu et dessine.
    Les threads réseau lui transmettent tout par une file (queue.Queue),
    donc la logique du jeu n'est jamais exécutée par deux threads à la fois.

Lancement :  python serveur.py
"""

import socket
import threading
import queue
import heapq
import itertools
import math
import random
import sys
import time
from collections import deque
from datetime import datetime

import pygame

from commun import (PORT, TERRAIN_L, TERRAIN_H, TAILLE, RAYON_PIECE, TICK_RATE,
                    COULEURS_TYPES, FREQUENTS, PALETTE_JOUEURS, FOND, FOND_PANNEAU,
                    BORDURE, TEXTE, TEXTE_GRIS, encoder, LecteurMessages, resumer,
                    Paquet, charger_polices, ecrire, ecrire_sur_fond, ouvrir_fenetre)

LARGEUR, HAUTEUR = 1200, 720
LATENCES_MS = [0, 100, 250, 500]
CENTRE = (320, 390)          # position du serveur dans le diagramme
RAYON_DIAGRAMME = 210        # distance serveur <-> clients dans le diagramme
DUREE_HANDSHAKE = 0.45       # durée de chaque étape de l'animation SYN / SYN-ACK / ACK


def ip_locale():
    """Adresse IP de ce PC sur le réseau local (celle que les autres joueurs doivent saisir)."""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("10.255.255.255", 1))   # aucun paquet n'est envoyé : on demande juste la route
        return s.getsockname()[0]
    except OSError:
        return "127.0.0.1"
    finally:
        s.close()


# ============================================================================
# Un client connecté
# ============================================================================
class ClientConnecte:
    def __init__(self, id_joueur, conn, adresse, couleur):
        self.id = id_joueur
        self.nom = f"J{id_joueur}"
        self.conn = conn
        self.adresse = f"{adresse[0]}:{adresse[1]}"
        self.couleur = couleur
        # état dans le jeu
        self.x = random.randint(40, TERRAIN_L - 40 - TAILLE)
        self.y = random.randint(40, TERRAIN_H - 40 - TAILLE)
        self.score = 0
        # état de la connexion
        self.pret = False         # True une fois le message "bienvenue" envoyé
        self.actif = True
        self.depuis = time.time()
        self.fin = None
        self.ping_ms = None
        # statistiques
        self.recus = self.envoyes = 0
        self.octets_recus = self.octets_envoyes = 0
        # file des messages à envoyer (lue par le thread d'envoi)
        self.file_envoi = queue.Queue()
        # position dans le diagramme
        self.pos = [float(CENTRE[0]), float(CENTRE[1])]
        self.cible = [float(CENTRE[0]), float(CENTRE[1])]


# ============================================================================
# Threads réseau
# ============================================================================
def thread_accepter(sock_serveur, boite):
    """Attend les nouveaux clients. accept() termine la poignée de main TCP."""
    while True:
        try:
            conn, adresse = sock_serveur.accept()
        except OSError:
            break  # socket serveur fermée -> arrêt
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        boite.put(("connexion", conn, adresse))


def thread_reception(client, boite):
    """Lit en boucle les messages d'un client et les transmet à la boucle principale."""
    lecteur = LecteurMessages()
    raison = "fermeture propre (FIN)"
    try:
        while True:
            donnees = client.conn.recv(4096)       # bloquant
            if not donnees:                        # b"" = le client a fermé la connexion
                break
            for message, taille in lecteur.ajouter(donnees):
                boite.put(("message", client, message, taille))
    except ConnectionResetError:
        raison = "connexion coupée brutalement (RST)"
    except OSError:
        raison = "connexion fermée"
    except (ValueError, UnicodeDecodeError):
        raison = "message invalide reçu"
    boite.put(("deconnexion", client, raison))


def thread_envoi(client):
    """Envoie les messages de la file du client, en respectant la latence simulée."""
    while True:
        element = client.file_envoi.get()
        if element is None:
            break
        moment_envoi, donnees = element
        attente = moment_envoi - time.time()
        if attente > 0:
            time.sleep(attente)
        try:
            client.conn.sendall(donnees)
        except OSError:
            break


# ============================================================================
# Le serveur
# ============================================================================
class Serveur:
    def __init__(self):
        self.boite = queue.Queue()      # threads réseau -> boucle principale
        self.en_attente = []            # tas (heap) d'actions à faire plus tard
        self.compteur = itertools.count()
        self.clients = {}
        self.prochain_id = 1
        self.latence_ms = 0
        self.tout_afficher = False
        self.journal = deque(maxlen=300)
        self.paquets = []
        self.piece = self.nouvelle_piece()
        self.debut = time.time()
        self.dernier_tick = 0.0
        self.total = {"recus": 0, "envoyes": 0, "o_recus": 0, "o_envoyes": 0}
        self.par_seconde = dict(self.total)
        self._total_precedent = dict(self.total)
        self._t_debit = time.time()
        self.sock = None
        self.titre = "SERVEUR DE JEU"
        self.ip = ip_locale()
        self.texte_attente = "En attente de joueurs... lance client.py"
        self.legende = [("SYN / ACK", "SYN"), ("bienvenue", "bienvenue"), ("deplacement", "deplacement"),
                        ("etat", "etat"), ("ping / pong", "ping"), ("chat", "chat"),
                        ("evenement", "evenement"), ("FIN / RST", "FIN")]
        self.aide = ("L : latence simulée   |   F : afficher/masquer deplacement & etat   |   "
                     "C : vider le journal   |   Échap : quitter")

    # ------------------------------------------------------------------ réseau
    def demarrer_reseau(self):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)   # IPv4 + TCP
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            self.sock.bind(("0.0.0.0", PORT))
        except OSError as e:
            print(f"Impossible d'utiliser le port {PORT} : {e}")
            print("Un serveur tourne peut-être déjà dans un autre terminal.")
            sys.exit(1)
        self.sock.listen()
        threading.Thread(target=thread_accepter, args=(self.sock, self.boite), daemon=True).start()
        self.journaliser("**", "", f"Serveur démarré, en écoute sur le port {PORT} (TCP)", "info")
        self.journaliser("**", "", f"Les autres PC doivent se connecter à {self.ip}", "info")
        self.journaliser("**", "", "En attente de joueurs...", "info")

    def envoyer(self, client, message, journal=True):
        """Met un message dans la file d'envoi du client (avec la latence simulée)."""
        donnees = encoder(message)
        latence = self.latence_ms / 1000
        client.file_envoi.put((time.time() + latence, donnees))
        client.envoyes += 1
        client.octets_envoyes += len(donnees)
        self.total["envoyes"] += 1
        self.total["o_envoyes"] += len(donnees)
        type_msg = message["type"]
        if journal:
            self.journaliser("->", client.nom, resumer(message), type_msg, len(donnees))
        self.paquets.append(Paquet(CENTRE, lambda c=client: c.pos, type_msg,
                                   duree=max(0.3, latence), etiquette=etiquette(type_msg)))

    def diffuser(self, message):
        """Envoie le même message à tous les joueurs (broadcast)."""
        cibles = [c for c in self.clients.values() if c.actif and c.pret]
        if not cibles:
            return
        for c in cibles:
            self.envoyer(c, message, journal=False)
        self.journaliser("->", "TOUS", resumer(message), message["type"], len(encoder(message)))

    def planifier(self, delai, action):
        heapq.heappush(self.en_attente, (time.time() + delai, next(self.compteur), ("action", action)))

    # ---------------------------------------------------------- boîte aux lettres
    def traiter_boite(self):
        maintenant = time.time()
        while True:
            try:
                element = self.boite.get_nowait()
            except queue.Empty:
                break
            if element[0] == "connexion":
                self.nouvelle_connexion(element[1], element[2])
                continue
            # Les messages reçus passent par la latence simulée avant d'être traités
            latence = self.latence_ms / 1000
            if element[0] == "message":
                client, message = element[1], element[2]
                type_msg = message.get("type", "?")
                self.paquets.append(Paquet(lambda c=client: c.pos, CENTRE, type_msg,
                                           duree=max(0.3, latence), etiquette=etiquette(type_msg)))
            heapq.heappush(self.en_attente, (maintenant + latence, next(self.compteur), element))

        while self.en_attente and self.en_attente[0][0] <= maintenant:
            _, _, element = heapq.heappop(self.en_attente)
            if element[0] == "message":
                self.traiter_message(element[1], element[2], element[3])
            elif element[0] == "deconnexion":
                self.deconnexion(element[1], element[2])
            elif element[0] == "action":
                element[1]()

    # ------------------------------------------------------------ connexions
    def nouvelle_connexion(self, conn, adresse):
        couleur = PALETTE_JOUEURS[(self.prochain_id - 1) % len(PALETTE_JOUEURS)]
        c = ClientConnecte(self.prochain_id, conn, adresse, couleur)
        self.prochain_id += 1
        self.clients[c.id] = c
        self.placer_noeuds()
        # le nouveau nœud arrive de l'extérieur du diagramme
        c.pos = [CENTRE[0] + (c.cible[0] - CENTRE[0]) * 1.4, CENTRE[1] + (c.cible[1] - CENTRE[1]) * 1.4]

        threading.Thread(target=thread_reception, args=(c, self.boite), daemon=True).start()
        threading.Thread(target=thread_envoi, args=(c,), daemon=True).start()

        # Animation de la poignée de main TCP (elle est faite par le système d'exploitation
        # pendant connect()/accept() ; ici on la montre au ralenti pour l'expliquer)
        d = DUREE_HANDSHAKE
        position = lambda: c.pos
        self.paquets.append(Paquet(position, CENTRE, "SYN", duree=d, delai=0, etiquette="SYN"))
        self.paquets.append(Paquet(CENTRE, position, "SYN-ACK", duree=d, delai=d, etiquette="SYN-ACK"))
        self.paquets.append(Paquet(position, CENTRE, "ACK", duree=d, delai=2 * d, etiquette="ACK"))
        self.journaliser("<-", c.nom, f"SYN depuis {c.adresse} (demande de connexion)", "SYN")
        self.planifier(d, lambda: self.journaliser("->", c.nom, "SYN-ACK (le serveur accepte)", "SYN-ACK"))
        self.planifier(2 * d, lambda: self.journaliser("<-", c.nom, "ACK -> connexion TCP établie", "ACK"))
        self.planifier(3 * d, lambda: self.accueillir(c))

    def accueillir(self, c):
        if not c.actif:
            return
        c.pret = True
        self.envoyer(c, {"type": "bienvenue", "id": c.id, "nom": c.nom,
                         "couleur": c.couleur, "x": c.x, "y": c.y})
        self.diffuser({"type": "evenement", "texte": f"{c.nom} a rejoint la partie"})

    def deconnexion(self, c, raison):
        if not c.actif:
            return
        c.actif = False
        c.fin = time.time()
        c.file_envoi.put(None)
        try:
            c.conn.close()
        except OSError:
            pass
        type_fin = "RST" if "RST" in raison else "FIN"
        self.paquets.append(Paquet(lambda: c.pos, CENTRE, type_fin, duree=0.5, etiquette=type_fin))
        self.journaliser("**", c.nom, f"déconnecté : {raison}", type_fin)
        self.quand_deconnexion(c)
        if c.pret:
            self.diffuser({"type": "evenement", "texte": f"{c.nom} a quitté la partie"})
        self.planifier(1.5, lambda: self.retirer(c))

    def quand_deconnexion(self, c):
        """Crochet : nettoyage côté jeu quand un client part (redéfini dans serveur_arene.py)."""

    def retirer(self, c):
        self.clients.pop(c.id, None)
        self.placer_noeuds()

    def placer_noeuds(self):
        actifs = [c for c in self.clients.values() if c.actif]
        n = len(actifs)
        for i, c in enumerate(actifs):
            angle = -math.pi / 2 + 2 * math.pi * i / max(1, n)
            c.cible = [CENTRE[0] + RAYON_DIAGRAMME * math.cos(angle),
                       CENTRE[1] + RAYON_DIAGRAMME * math.sin(angle)]

    # ------------------------------------------------------------ logique du jeu
    def nouvelle_piece(self):
        return [random.randint(30, TERRAIN_L - 30), random.randint(30, TERRAIN_H - 30)]

    def traiter_message(self, c, message, taille):
        c.recus += 1
        c.octets_recus += taille
        self.total["recus"] += 1
        self.total["o_recus"] += taille
        type_msg = message.get("type", "?")
        self.journaliser("<-", c.nom, resumer(message), type_msg, taille)
        if not c.actif or not c.pret:
            return
        try:
            self.logique_message(c, type_msg, message)
        except (KeyError, TypeError, ValueError):
            self.journaliser("**", c.nom, "message invalide ignoré", "erreur")

    def logique_message(self, c, type_msg, message):
        """Ce que fait le jeu quand un message arrive (redéfini dans serveur_arene.py)."""
        if True:
            if type_msg == "deplacement":
                # Le serveur fait autorité : il vérifie que la position reste dans le terrain
                c.x = max(0, min(TERRAIN_L - TAILLE, int(message["x"])))
                c.y = max(0, min(TERRAIN_H - TAILLE, int(message["y"])))
                px, py = self.piece
                if (c.x - RAYON_PIECE < px < c.x + TAILLE + RAYON_PIECE and
                        c.y - RAYON_PIECE < py < c.y + TAILLE + RAYON_PIECE):
                    c.score += 1
                    self.piece = self.nouvelle_piece()
                    self.diffuser({"type": "evenement",
                                   "texte": f"{c.nom} a ramassé une pièce (score : {c.score})"})
            elif type_msg == "ping":
                c.ping_ms = message.get("rtt")
                self.envoyer(c, {"type": "pong", "t": message["t"]})
            elif type_msg == "chat":
                texte = str(message.get("texte", ""))[:80].strip()
                if texte:
                    self.diffuser({"type": "chat", "id": c.id, "nom": c.nom, "texte": texte})

    def tick(self):
        """20 fois par seconde : envoyer l'état complet du jeu à tous les joueurs."""
        maintenant = time.time()
        if maintenant - self.dernier_tick < 1 / TICK_RATE:
            return
        self.dernier_tick = maintenant
        prets = [c for c in self.clients.values() if c.actif and c.pret]
        if not prets:
            return
        etat = {
            "type": "etat",
            "joueurs": {str(c.id): {"nom": c.nom, "x": c.x, "y": c.y,
                                    "couleur": c.couleur, "score": c.score} for c in prets},
            "piece": self.piece,
        }
        for c in prets:
            self.envoyer(c, etat, journal=False)
        self.journaliser("->", "TOUS", resumer(etat), "etat", len(encoder(etat)))

    def calculer_debit(self):
        maintenant = time.time()
        if maintenant - self._t_debit >= 1:
            ecoule = maintenant - self._t_debit
            for cle in self.total:
                self.par_seconde[cle] = (self.total[cle] - self._total_precedent[cle]) / ecoule
            self._total_precedent = dict(self.total)
            self._t_debit = maintenant

    # ------------------------------------------------------------------ journal
    def journaliser(self, sens, qui, texte, type_msg, octets=None):
        if type_msg in FREQUENTS and not self.tout_afficher:
            return
        heure = datetime.now().strftime("%H:%M:%S.%f")[:-3]
        ligne = f"{heure} {sens} {qui:<4} {texte}"
        if octets:
            ligne += f"  ({octets} o)"
        self.journal.append((ligne, COULEURS_TYPES.get(type_msg, TEXTE)))
        if type_msg not in FREQUENTS and type_msg not in ("ping", "pong"):
            print(ligne)   # copie dans le terminal (sans les messages trop fréquents)

    # ================================================================ affichage
    def dessiner(self, ecran, p):
        ecran.fill(FOND)
        maintenant = time.time()
        self.dessiner_barre(ecran, p, maintenant)
        self.dessiner_diagramme(ecran, p, maintenant)
        self.dessiner_tableau(ecran, p, maintenant)
        self.dessiner_journal(ecran, p)
        ecrire(ecran, p["petit"], self.aide, (650, HAUTEUR - 22), TEXTE_GRIS)

    def texte_barre(self):
        return f"tick rate : {TICK_RATE}/s"

    def dessiner_barre(self, ecran, p, maintenant):
        pygame.draw.rect(ecran, FOND_PANNEAU, (0, 0, LARGEUR, 58))
        pygame.draw.line(ecran, BORDURE, (0, 58), (LARGEUR, 58))
        ecrire(ecran, p["titre"], self.titre, (16, 8))
        duree = int(maintenant - self.debut)
        nb = len([c for c in self.clients.values() if c.actif])
        couleur_lat = (255, 120, 90) if self.latence_ms else TEXTE_GRIS
        ecrire(ecran, p["normal"], f"IP à donner aux joueurs : {self.ip}:{PORT}   |   depuis {duree // 60:02d}:{duree % 60:02d}"
               f"   |   {nb} client(s)   |   {self.texte_barre()}", (210, 8), TEXTE)
        r = ecrire(ecran, p["normal"], f"Reçus : {self.total['recus']} paquets "
                   f"({self.par_seconde['recus']:.0f}/s, {self.par_seconde['o_recus'] / 1024:.1f} Ko/s)"
                   f"     Envoyés : {self.total['envoyes']} paquets "
                   f"({self.par_seconde['envoyes']:.0f}/s, {self.par_seconde['o_envoyes'] / 1024:.1f} Ko/s)",
                   (210, 32), TEXTE_GRIS)
        ecrire(ecran, p["gras"], f"Latence simulée : {self.latence_ms} ms [L]", (r.right + 25, 32), couleur_lat)

    def dessiner_diagramme(self, ecran, p, maintenant):
        ecrire(ecran, p["gras"], "Topologie du réseau (paquets en direct)", (16, 70), TEXTE_GRIS)

        self.dessiner_carte(ecran, p)

        # câbles
        self._dessiner_cables_et_noeuds(ecran, p, maintenant)

    def dessiner_carte(self, ecran, p):
        """Mini-carte : l'état du jeu tel que le serveur le connaît."""
        carte = pygame.Rect(452, 92, TERRAIN_L // 4, TERRAIN_H // 4)
        ecrire(ecran, p["petit"], "État du jeu (vu par le serveur)", (carte.x, carte.y - 17), TEXTE_GRIS)
        pygame.draw.rect(ecran, (12, 14, 20), carte)
        pygame.draw.rect(ecran, BORDURE, carte, 1)
        pygame.draw.circle(ecran, (255, 215, 80), (carte.x + self.piece[0] // 4, carte.y + self.piece[1] // 4), 3)
        for c in self.clients.values():
            if c.actif and c.pret:
                pygame.draw.rect(ecran, c.couleur, (carte.x + c.x // 4, carte.y + c.y // 4, TAILLE // 4, TAILLE // 4))

    def _dessiner_cables_et_noeuds(self, ecran, p, maintenant):
        for c in self.clients.values():
            c.pos[0] += (c.cible[0] - c.pos[0]) * 0.12
            c.pos[1] += (c.cible[1] - c.pos[1]) * 0.12
            couleur = (70, 75, 100) if c.actif else (90, 40, 40)
            pygame.draw.line(ecran, couleur, CENTRE, c.pos, 2)

        # paquets en vol
        self.paquets = [pq for pq in self.paquets if not pq.fini()][-500:]
        for pq in self.paquets:
            pq.dessiner(ecran, p["petit"])

        # serveur
        boite = pygame.Rect(0, 0, 150, 64)
        boite.center = CENTRE
        pygame.draw.rect(ecran, (40, 52, 85), boite, border_radius=10)
        pygame.draw.rect(ecran, (120, 150, 230), boite, 2, border_radius=10)
        ecrire(ecran, p["gras"], "SERVEUR", (CENTRE[0], CENTRE[1] - 10), centre=True)
        ecrire(ecran, p["petit"], f"{self.ip}:{PORT}", (CENTRE[0], CENTRE[1] + 12), TEXTE_GRIS, centre=True)

        # clients
        for c in self.clients.values():
            x, y = int(c.pos[0]), int(c.pos[1])
            if c.actif:
                couleur = c.couleur
            else:
                a = max(0.0, 1 - (maintenant - c.fin) / 1.5)
                couleur = [int(FOND[i] + (c.couleur[i] - FOND[i]) * a * 0.5) for i in range(3)]
            pygame.draw.circle(ecran, couleur, (x, y), 28)
            pygame.draw.circle(ecran, (255, 255, 255) if c.actif else (120, 60, 60), (x, y), 28, 2)
            ecrire(ecran, p["gras"], c.nom, (x, y), (15, 15, 20), centre=True)
            # étiquettes au-dessus du nœud s'il est en haut (pour ne pas cacher le câble)
            dy1, dy2 = (-56, -40) if y < CENTRE[1] - 10 else (40, 56)
            ecrire_sur_fond(ecran, p["petit"], c.adresse, (x, y + dy1), TEXTE_GRIS, FOND)
            if not c.actif:
                info = "déconnecté"
            elif not c.pret:
                info = "poignée de main..."
            else:
                info = self.info_noeud(c)
            ecrire_sur_fond(ecran, p["petit"], info, (x, y + dy2), TEXTE, FOND)

        if not self.clients:
            ecrire(ecran, p["normal"], self.texte_attente, (CENTRE[0], CENTRE[1] + 70),
                   TEXTE_GRIS, centre=True)

        # légende
        for i, (nom, type_msg) in enumerate(self.legende):
            lx = 20 + (i % 4) * 155
            ly = 668 + (i // 4) * 22
            pygame.draw.circle(ecran, COULEURS_TYPES[type_msg], (lx + 6, ly + 8), 6)
            ecrire(ecran, p["petit"], nom, (lx + 18, ly + 1), TEXTE_GRIS)

    def info_noeud(self, c):
        ping = f"{c.ping_ms} ms" if c.ping_ms is not None else "..."
        return f"ping {ping}  |  score {c.score}"

    def dessiner_tableau(self, ecran, p, maintenant):
        x0 = 650
        pygame.draw.rect(ecran, FOND_PANNEAU, (x0 - 10, 64, LARGEUR - x0 + 4, HAUTEUR - 64 - 30), border_radius=8)
        ecrire(ecran, p["gras"], "Clients connectés", (x0, 72), TEXTE_GRIS)
        colonnes = [("ID", 0), ("Adresse IP:port", 40), ("Connecté", 190), ("Reçus", 262),
                    ("Envoyés", 325), ("Octets in/out", 395), ("Ping", 490)]
        for nom, dx in colonnes:
            ecrire(ecran, p["petit"], nom, (x0 + dx, 96), TEXTE_GRIS)
        pygame.draw.line(ecran, BORDURE, (x0, 113), (LARGEUR - 16, 113))
        actifs = [c for c in self.clients.values() if c.actif]
        for i, c in enumerate(actifs[:6]):
            y = 118 + i * 21
            d = int(maintenant - c.depuis)
            pygame.draw.rect(ecran, c.couleur, (x0, y + 3, 10, 10))
            valeurs = [(c.nom, 14), (c.adresse, 40), (f"{d // 60:02d}:{d % 60:02d}", 190), (str(c.recus), 262),
                       (str(c.envoyes), 325),
                       (f"{c.octets_recus / 1024:.1f}/{c.octets_envoyes / 1024:.1f} Ko", 395),
                       (f"{c.ping_ms} ms" if c.ping_ms is not None else "-", 490)]
            for texte, dx in valeurs:
                ecrire(ecran, p["mono"], texte, (x0 + dx, y + 1))
        if not actifs:
            ecrire(ecran, p["petit"], "aucun client", (x0, 120), TEXTE_GRIS)
        if len(actifs) > 6:
            ecrire(ecran, p["petit"], f"+ {len(actifs) - 6} autre(s)", (x0, 118 + 6 * 21), TEXTE_GRIS)

    def dessiner_journal(self, ecran, p):
        x0, y0 = 650, 262
        pygame.draw.line(ecran, BORDURE, (x0, y0 - 8), (LARGEUR - 16, y0 - 8))
        filtre = "affichés" if self.tout_afficher else "masqués"
        ecrire(ecran, p["gras"], "Journal réseau", (x0, y0), TEXTE_GRIS)
        ecrire(ecran, p["petit"], f"<- reçu   -> envoyé   ** système      deplacement/etat : {filtre} [F]",
               (x0 + 125, y0 + 3), TEXTE_GRIS)
        hauteur_ligne = 16
        nb_lignes = (HAUTEUR - 40 - (y0 + 26)) // hauteur_ligne
        lignes = list(self.journal)[-nb_lignes:]
        for i, (texte, couleur) in enumerate(lignes):
            if len(texte) > 72:
                texte = texte[:71] + "…"
            ecrire(ecran, p["mono"], texte, (x0, y0 + 26 + i * hauteur_ligne), couleur)

    def touche(self, touche):
        """Crochet : touches supplémentaires (redéfini dans serveur_arene.py)."""

    # ============================================================ boucle principale
    def executer(self):
        pygame.init()
        ecran = ouvrir_fenetre(LARGEUR, HAUTEUR)
        pygame.display.set_caption(f"{self.titre} - port {PORT}")
        polices = charger_polices()
        horloge = pygame.time.Clock()
        self.demarrer_reseau()

        en_cours = True
        while en_cours:
            for ev in pygame.event.get():
                if ev.type == pygame.QUIT:
                    en_cours = False
                elif ev.type == pygame.KEYDOWN:
                    if ev.key == pygame.K_ESCAPE:
                        en_cours = False
                    elif ev.key == pygame.K_l:
                        i = (LATENCES_MS.index(self.latence_ms) + 1) % len(LATENCES_MS)
                        self.latence_ms = LATENCES_MS[i]
                        self.journaliser("**", "", f"Latence simulée réglée sur {self.latence_ms} ms", "info")
                    elif ev.key == pygame.K_f:
                        self.tout_afficher = not self.tout_afficher
                    elif ev.key == pygame.K_c:
                        self.journal.clear()
                    else:
                        self.touche(ev.key)

            self.traiter_boite()
            self.tick()
            self.calculer_debit()
            self.dessiner(ecran, polices)
            pygame.display.flip()
            horloge.tick(60)

        # arrêt propre
        print("Arrêt du serveur")
        for c in list(self.clients.values()):
            try:
                c.conn.close()
            except OSError:
                pass
        self.sock.close()
        pygame.quit()


def etiquette(type_msg):
    """Texte affiché à côté d'un paquet (pas pour les paquets fréquents, illisible)."""
    return None if type_msg in FREQUENTS else type_msg


if __name__ == "__main__":
    Serveur().executer()
