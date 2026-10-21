using Ro3Emu.Server.Game;

namespace Ro3Emu.FakePlayers;

/// <summary>Character names that look like real players: "Matador", "OUpador", "LucasRagnarok", "xSombrax"...</summary>
public static class FakeNames
{
    private static readonly string[] Words =
    [
        "Matador", "Upador", "Farmador", "Ceifador", "Sombra", "Lobo", "Dragao", "Fenix", "Guerreiro", "Mago",
        "Arqueiro", "Ninja", "Viking", "Templario", "Cacador", "Paladino", "Lendario", "Brabo", "Ragnar", "Odin",
        "Thor", "Loki", "Valquiria", "Lua", "Estrela", "Tempestade", "Trovao", "Raio", "Gelo", "Fogo",
        "Corvo", "Falcao", "Tigre", "Pantera", "Leao", "Urso", "Sabio", "Feiticeira", "Bruxa", "Princesa",
        "Rainha", "Rei", "Duque", "Pirata", "Samurai", "Monge", "Assassino", "Espadachim", "Ferreiro", "Bardo",
        "Poring", "Baphomet", "Osiris", "Drake", "Eddga", "Maya", "Valhalla", "Midgard", "Prontera", "Geffen",
        "Morroc", "Payon", "Juno", "Yggdrasil", "Emperium", "Mjolnir", "Excalibur", "Runa", "Mistico", "Caotico",
        "Noturno", "Imortal", "Supremo", "Selvagem", "Sagrado", "Maldito", "Divino", "Eterno", "Furioso", "Sereno",
        "Killer", "Hunter", "Slayer", "Shadow", "Storm", "Blade", "Ghost", "Reaper", "Knight", "Angel",
        "Demon", "Wolf", "Phoenix", "Legend", "Master", "Sniper", "Healer", "Tank", "Noob", "Pro",
    ];

    private static readonly string[] People =
    [
        "Lucas", "Gabriel", "Rafa", "Bia", "Juh", "Nanda", "Pedrinho", "Joao", "Ana", "Carol",
        "Thiago", "Bruno", "Leo", "Duda", "Mari", "Gui", "Feh", "Vini", "Lari", "Igor",
        "Caio", "Lipe", "Babi", "Tati", "Diego", "Matheus", "Julia", "Bela", "Davi", "Nina",
        "Rodrigo", "Paulinha", "Kaique", "Leticia", "Renan", "Sofia", "Enzo", "Alice", "Murilo", "Clara",
    ];

    private static readonly string[] Prefixes = ["O", "Sr", "Lord", "Dark", "Mega", "Lil", "Big", "Mestre", "Dama", "Rei", "Tio", "Xx"];
    private static readonly string[] Suffixes = ["BR", "RO", "Gamer", "Top", "Br", "God", "Zika", "Lindo", "Pro", "XD", "S2", "Jr"];

    /// <summary>Candidate names for player <paramref name="index"/>, the preferred one first (2-16 characters each).</summary>
    public static IEnumerable<string> Candidates(int index)
    {
        var r = new Random(unchecked(index * 104729 + 3));
        for (int attempt = 0; attempt < 40; attempt++)
        {
            string name = Compose(r, attempt);
            if (name.Length is >= 2 and <= 16) yield return name;
        }
        yield return $"Aventureiro{index}";
    }

    private static string Compose(Random r, int attempt)
    {
        string word = Words[r.Next(Words.Length)], person = People[r.Next(People.Length)];
        int pattern = r.Next(attempt == 0 ? 10 : 12);
        string name = pattern switch
        {
            0 => word,
            1 => Prefixes[r.Next(Prefixes.Length)] + word,
            2 => word + Suffixes[r.Next(Suffixes.Length)],
            3 => person + word,
            4 => person + Suffixes[r.Next(Suffixes.Length)],
            5 => "x" + word + "x",
            6 => word + r.Next(1, 100),
            7 => person + "_" + word,
            8 => person + r.Next(10, 2000),
            9 => word + Words[r.Next(Words.Length)],
            10 => Prefixes[r.Next(Prefixes.Length)] + person,
            _ => word + "_" + r.Next(1, 1000),
        };
        // Late attempts add a number so a taken name still finds a free variant.
        if (attempt >= 6 && !char.IsDigit(name[^1])) name += r.Next(1, 100);
        return name;
    }
}

/// <summary>Lines a fake player says in map chat (never starting with ! or /, those are commands).</summary>
public static class FakeChat
{
    private static readonly string[] Town =
    [
        "bom dia galera", "boa noite pessoal", "alguem pt pra upar?", "vendo carta poring, chama pm", "kkkkkk",
        "alguem sabe onde dropa elunium?", "compro oridecon", "server ta show", "quem vai na woe hoje?",
        "procuro guild ativa", "vendo pocao vermelha barata", "eae", "lag ai tambem?", "q classe vcs recomendam?",
        "acabei de virar {job}", "nivel {level} finalmente", "alguem quer duelar?", "partiu upar", "afk 5 min",
        "brb", "vlw galera", "quem ta na guild procurando membro?", "alguem tem refinador?", "to sem zeny kkk",
        "esse evento ta bom demais", "ja pegaram o bonus de exp?", "exp ta voando hoje", "{job} ou {other}? to na duvida",
        "alguem me ajuda na quest?", "onde fica o kafra?", "vendo equipamento, pm", "+1 pra pt", "ola!", "salve salve",
    ];

    private static readonly string[] Field =
    [
        "upando aqui", "drop ta bom aqui", "alguem pt?", "quase morri kkk", "esses bichos batem forte",
        "farmando zeny", "mais um nivel!", "dropou carta!!! mentira kkk", "morri de novo", "bora upar",
        "aqui ta cheio hein", "alguem com cura?", "to sem pot", "nivel {level} chegando",
    ];

    private static readonly string[] Classes = ["Cavaleiro", "Bruxo", "Cacador", "Sacerdote", "Mercenario", "Ferreiro", "Templario", "Monge"];

    public static string Line(Random r, bool hunting, string job, int level)
    {
        var pool = hunting && r.NextDouble() < 0.7 ? Field : Town;
        return pool[r.Next(pool.Length)]
            .Replace("{job}", job)
            .Replace("{level}", level.ToString())
            .Replace("{other}", Classes[r.Next(Classes.Length)]);
    }
}
