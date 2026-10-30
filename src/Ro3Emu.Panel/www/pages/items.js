// The item database: every item of the game's data, searchable, and a page per item with its stats, the monsters
// that drop it and the shops that sell it (/api/items).
'use strict';

async function itemsPage() {
  const query = queryOf();
  const page = Number(query.page) || 1;
  const result = await api('GET', '/api/items' + listHref('', { ...query, page }));
  const filters = filterForm('/items', query, [
    optionSelect('type', 'Type', result.types, query.type, 'Every type'),
    optionSelect('quality', 'Quality', result.qualities, query.quality, 'Every quality'),
    levelRange(query),
  ], 'Name or ID');
  const rows = result.items.length
    ? table([
      { label: 'Item', grow: true, value: i => itemName(i.id, i.name, i.quality) },
      { label: 'ID', num: true, opt: true, value: i => mono(String(i.id)) },
      { label: 'Type', value: i => [i.typeName, i.subType && h('span', { class: 'muted' }, ' · ' + i.subType)] },
      { label: 'Quality', opt: true, value: i => chip('q' + i.quality, i.qualityName) },
      { label: 'Level', num: true, value: i => i.level > 0 ? num(i.level) : h('span', { class: 'muted' }, '-') },
    ], result.items, { onrow: i => navigate('/items/' + i.id) })
    : emptyState('search', 'No item matches', 'Try part of the name, or fewer filters.');
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { label: 'Items' }], title: 'Items',
    subtitle: 'Every item in the game, from the server\'s own data.', actions: densityToggle() }),
  h('section', { class: 'card' }, h('div', { class: 'card-head' }, filters), rows,
    h('div', { class: 'card-foot' }, listPager(result, p => listHref('/items', { ...query, page: p }), 'item')))];
}

async function itemPage(id) {
  const item = await api('GET', '/api/items/' + encodeURIComponent(id));
  const i = item.item;
  const dropped = item.droppedBy.length
    ? table([
      { label: 'Monster', grow: true, value: d => monsterName(d.monsterId, d.monster, d.rank) },
      { label: 'Level', num: true, value: d => num(d.level) },
      { label: 'Rank', opt: true, value: d => rankChip(d.rank) || h('span', { class: 'muted' }, 'Normal') },
      { label: 'How', value: d => d.kind ? chip(d.kind === 'Rare winner' ? 'event' : 'info', 'MVP: ' + d.kind) : h('span', { class: 'muted' }, 'Drop') },
      { label: 'Count', num: true, opt: true, value: d => countRange(d.min, d.max) },
      { label: 'Chance', num: true, value: d => percent(d.chance) },
    ], item.droppedBy, { onrow: d => navigate('/monsters/' + d.monsterId) })
    : emptyState('paw', 'No monster drops it', 'It comes from quests, shops, crafting or chests.');
  const shops = item.shops.length
    ? table([
      { label: 'Shop', grow: true, value: s => h('span', { class: 'strong' }, s.shop) },
      { label: 'Price', num: true, value: s => s.price > 0 ? num(s.price) : 'Free' },
      { label: 'Paid in', value: s => s.currencyId > 0 ? link('/items/' + s.currencyId, { class: 'text-link' }, s.currency) : '-' },
      { label: 'Per buy', num: true, opt: true, value: s => num(s.count) },
    ], item.shops, { short: true })
    : null;
  const stats = item.stats.length
    ? facts(item.stats.map(s => [s.name, h('span', { class: 'num strong' }, s.value)]))
    : null;
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { href: '/items', label: 'Items' }, { label: i.name }],
    title: i.name, after: [chip('q' + i.quality, i.qualityName), chip('plain', i.typeName)],
    meta: [['ID', mono(String(i.id))], ['Type', [i.typeName, i.subType ? ' · ' + i.subType : '']], ['Level', i.level > 0 ? num(i.level) : 'Any']] }),
  h('div', { class: 'grid' },
    h('div', { class: 'span-8 stack' },
      card('Description', null, body(h('p', { class: 'desc' }, item.description || 'The game gives no description.'))),
      card('Dropped by', null, dropped, item.droppedBy.length ? 'Chance per kill that at least one drops; a player\'s level and class limits are not counted.' : null),
      shops && card('Sold in shops', null, shops)),
    h('div', { class: 'span-4 stack' },
      card('Item', null, body(facts([
        ['Quality', h('span', { class: 'strong q' + i.quality }, i.qualityName)],
        ['Required level', i.level > 0 ? h('span', { class: 'num' }, num(i.level)) : 'Any'],
        ['Classes', item.classes.length ? item.classes.join(', ') : 'Every class'],
        ['Trade', item.trade],
        ['Stack', item.stackLimit > 1 ? h('span', { class: 'num' }, num(item.stackLimit)) : 'Does not stack'],
        ['Usable', item.usable ? 'Yes' : 'No']]))),
      stats && card('Stats', null, body(stats))))];
}

routes.push([/^\/items$/, itemsPage, 'items'], [/^\/items\/(\d+)$/, itemPage, 'items']);
titles.items = 'Items';

// The items and monsters are public pages now: the admin sidebar's placeholder for them goes.
{
  const later = laterSections.findIndex(([label]) => label === 'Items & monsters');
  if (later >= 0) laterSections.splice(later, 1);
}
