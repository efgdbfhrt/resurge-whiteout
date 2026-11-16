-- Resurge Whiteout in-app updates (2.7+). Run once in the Supabase SQL editor (safe to run again).
-- The launcher reads the newest row (anonymous, read-only) and downloads its parts from the public
-- `releases` storage bucket. Only the project owner (dashboard / service role) can publish rows or files.

create table if not exists public.launcher_releases (
  version text primary key check (version ~ '^[0-9]+\.[0-9]+(\.[0-9]+)?$'),
  urls text[] not null check (cardinality(urls) between 1 and 10),
  sha256 text not null check (sha256 ~ '^[0-9a-f]{64}$'),
  size bigint not null check (size > 0),
  notes text not null default '' check (char_length(notes) <= 500),
  published_at timestamptz not null default now()
);
alter table public.launcher_releases enable row level security;
drop policy if exists releases_read on public.launcher_releases;
create policy releases_read on public.launcher_releases for select to anon, authenticated using (true);
-- No insert/update/delete policies: players can never publish an update.

-- Public bucket for the exe parts (each part < 50 MB, the free-plan upload limit).
insert into storage.buckets (id, name, public) values ('releases', 'releases', true)
on conflict (id) do update set public = true;
