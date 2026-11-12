"""
COMMUN.PY : code partagé par le serveur et le client
====================================================
- les réglages communs (port, taille du terrain, tick rate)
- l'encodage des messages (JSON + '\\n')
- le découpage du flux TCP en messages
- l'animation des paquets qui voyagent sur le réseau
"""

import json
import time
import pygame

# ----------------------------------------------------------------------------
# Réglages communs
# ----------------------------------------------------------------------------
PORT = 5050                    # (5000 est pris par AirPlay sur Mac)
TERRAIN_L, TERRAIN_H = 700, 540
TAILLE = 36                    # taille d'un joueur (carré)
RAYON_PIECE = 10
TICK_RATE = 20                 # le serveur envoie l'état du jeu 20 fois par seconde

# Couleurs des types de messages (dans les animations et le journal)
COULEURS_TYPES = {
    "SYN":         (255, 255, 255),
    "SYN-ACK":     (200, 200, 200),
    "ACK":         (255, 255, 255),
    "bienvenue":   (120, 230, 255),
    "profil":      (120, 230, 255),
    "deplacement": (90, 150, 255),
    "entree":      (90, 150, 255),
    "etat":        (80, 210, 130),
    "ping":        (255, 215, 80),
    "pong":        (255, 160, 50),
    "chat":        (255, 120, 200),
    "evenement":   (190, 140, 255),
    "FIN":         (255, 90, 90),
    "RST":         (255, 60, 60),
    "info":        (150, 150, 165),
    "erreur":      (255, 90, 90),
}

# Messages envoyés très souvent (20 fois/s) : petits points, masqués du journal par défaut
FREQUENTS = {"deplacement", "etat", "entree"}

PALETTE_JOUEURS = [
    [255, 99, 99], [99, 200, 255], [255, 200, 70], [140, 230, 120],
    [200, 130, 255], [255, 150, 60], [80, 230, 210], [255, 120, 190],
]

FOND = (18, 20, 28)
FOND_PANNEAU = (26, 29, 40)
BORDURE = (55, 60, 80)
TEXTE = (225, 228, 235)
TEXTE_GRIS = (140, 145, 160)


# ----------------------------------------------------------------------------
# Encodage / décodage des messages
# ----------------------------------------------------------------------------
def encoder(message):
    """
    Dictionnaire Python -> octets à envoyer.
    On termine chaque message par '\\n' car TCP est un FLUX d'octets :
    il ne garde pas les limites entre les messages (deux messages peuvent
    arriver collés, ou un message peut arriver en deux morceaux).
    """
    return (json.dumps(message, ensure_ascii=False) + "\n").encode("utf-8")


class LecteurMessages:
    """Reçoit des morceaux du flux TCP et en extrait les messages complets."""

    def __init__(self):
        self.tampon = b""

    def ajouter(self, donnees):
        self.tampon += donnees
        messages = []
        while b"\n" in self.tampon:
            ligne, self.tampon = self.tampon.split(b"\n", 1)
            if ligne.strip():
                messages.append((json.loads(ligne.decode("utf-8")), len(ligne) + 1))
        return messages  # liste de (message, taille en octets)


def resumer(message):
    """Description courte d'un message, pour le journal."""
    t = message.get("type", "?")
    if t == "deplacement":
        return f"deplacement x={message.get('x')} y={message.get('y')}"
    if t == "entree":
        tir = " TIR" if message.get("tir") else ""
        return f"entree #{message.get('seq')} dir=({message.get('dx')},{message.get('dy')}){tir}"
    if t == "etat":
        balles = f", {len(message['balles'])} balle(s)" if "balles" in message else ""
        return f"etat ({len(message.get('joueurs', {}))} joueur(s){balles})"
    if t == "bienvenue":
        return f"bienvenue id={message.get('id')} pos=({message.get('x')},{message.get('y')})"
    if t == "profil":
        return f'profil nom="{message.get("nom", "")}" perso={message.get("skin", "")}'
    if t == "ping":
        return "ping"
    if t == "pong":
        return "pong"
    if t == "chat":
        if "nom" in message:
            return f'chat {message["nom"]} : "{message.get("texte", "")}"'
        return f'chat "{message.get("texte", "")}"'
    if t == "evenement":
        return f"evenement : {message.get('texte', '')}"
    return t


# ----------------------------------------------------------------------------
# Affichage
# ----------------------------------------------------------------------------
def charger_polices():
    sans = "helveticaneue,helvetica,arial,dejavusans"
    mono = "menlo,consolas,dejavusansmono,couriernew"
    return {
        "titre": pygame.font.SysFont(sans, 22, bold=True),
        "gras": pygame.font.SysFont(sans, 15, bold=True),
        "normal": pygame.font.SysFont(sans, 15),
        "petit": pygame.font.SysFont(sans, 12),
        "mono": pygame.font.SysFont(mono, 12),
    }


def ecrire(ecran, police, texte, pos, couleur=TEXTE, centre=False):
    surface = police.render(str(texte), True, couleur)
    rect = surface.get_rect()
    if centre:
        rect.center = (int(pos[0]), int(pos[1]))
    else:
        rect.topleft = (int(pos[0]), int(pos[1]))
    ecran.blit(surface, rect)
    return rect


def ecrire_sur_fond(ecran, police, texte, centre, couleur, fond):
    """Texte centré avec un petit fond, pour rester lisible par-dessus les câbles."""
    surface = police.render(str(texte), True, couleur)
    rect = surface.get_rect()
    rect.center = (int(centre[0]), int(centre[1]))
    pygame.draw.rect(ecran, fond, rect.inflate(8, 2), border_radius=4)
    ecran.blit(surface, rect)


def ouvrir_fenetre(largeur, hauteur):
    """
    Fenêtre redimensionnable : on peut la réduire avec la souris (coin en bas à droite)
    pour mettre le serveur et les clients côte à côte ; le contenu s'adapte.
    """
    try:
        return pygame.display.set_mode((largeur, hauteur), pygame.RESIZABLE | pygame.SCALED)
    except pygame.error:
        return pygame.display.set_mode((largeur, hauteur))


def _position(p):
    """Une position peut être fixe (tuple) ou changer (fonction qui renvoie la position)."""
    return p() if callable(p) else p


class Paquet:
    """Un point coloré qui voyage d'un endroit à un autre = un message sur le réseau."""

    def __init__(self, depart, arrivee, type_msg, duree=0.35, delai=0.0, etiquette=None):
        self.depart = depart
        self.arrivee = arrivee
        self.type = type_msg
        self.duree = max(0.05, duree)
        self.debut = time.time() + delai
        self.etiquette = etiquette

    def fini(self):
        return time.time() >= self.debut + self.duree

    def dessiner(self, ecran, police=None):
        maintenant = time.time()
        if maintenant < self.debut:
            return
        p = min(1.0, (maintenant - self.debut) / self.duree)
        ax, ay = _position(self.depart)
        bx, by = _position(self.arrivee)
        x, y = int(ax + (bx - ax) * p), int(ay + (by - ay) * p)
        couleur = COULEURS_TYPES.get(self.type, (200, 200, 200))
        if self.type in FREQUENTS:
            pygame.draw.circle(ecran, couleur, (x, y), 4)
        else:
            pygame.draw.circle(ecran, couleur, (x, y), 7)
            pygame.draw.circle(ecran, (255, 255, 255), (x, y), 7, 1)
            if self.etiquette and police is not None:
                ecrire(ecran, police, self.etiquette, (x + 10, y - 18), couleur)
