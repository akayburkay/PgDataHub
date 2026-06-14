namespace PgDataHub.Metadata;

internal static class MetadataSql
{
    public const string Collections = """
        with collections as (
            select
                table_schema,
                table_name,
                table_type
            from information_schema.tables
            where table_schema not in
            (
                'pg_catalog',
                'information_schema',
                'topology'
            )
            and table_name not in
            (
                'geometry_columns',
                'geography_columns',
                'spatial_ref_sys',
                'raster_columns',
                'raster_overviews'
            )

            union all

            select
                schemaname,
                matviewname,
                'MATERIALIZED VIEW'
            from pg_matviews
            where schemaname not in
            (
                'pg_catalog',
                'information_schema',
                'topology'
            )
        )

        select
            c.table_schema,
            c.table_name,
            c.table_type,
            g.f_geometry_column,
            g.type,
            g.srid,
            pk.attname

        from collections c

        left join geometry_columns g
            on g.f_table_schema = c.table_schema
           and g.f_table_name = c.table_name

        left join pg_index i
            on i.indrelid =
                to_regclass(
                    quote_ident(c.table_schema)
                    || '.'
                    || quote_ident(c.table_name)
                )
           and i.indisprimary

        left join pg_attribute pk
            on pk.attrelid = i.indrelid
           and pk.attnum = any(i.indkey)

        order by
            c.table_schema,
            c.table_name
        """;

    public const string Columns = """
        select
            n.nspname,
            c.relname,
            a.attname,
            format_type(a.atttypid, a.atttypmod),
            not a.attnotnull

        from pg_attribute a

        join pg_class c
            on c.oid = a.attrelid

        join pg_namespace n
            on n.oid = c.relnamespace

        where
            a.attnum > 0
            and not a.attisdropped
            and c.relkind in ('r','v','m')
            and n.nspname not in
            (
                'pg_catalog',
                'information_schema',
                'topology'
            )

        order by
            n.nspname,
            c.relname,
            a.attnum
        """;

    public const string Functions = """
        select
            n.nspname,
            p.proname,
            pg_get_function_arguments(p.oid),
            pg_get_function_result(p.oid),
            d.description

        from pg_proc p

        join pg_namespace n
            on n.oid = p.pronamespace

        left join pg_description d
            on d.objoid = p.oid
           and d.objsubid = 0

        left join pg_depend dep
            on dep.objid = p.oid
           and dep.deptype = 'e'

        left join pg_extension ext
            on ext.oid = dep.refobjid

        where
            p.prokind = 'f'
            and ext.extname is null
            and n.nspname not in
            (
                'pg_catalog',
                'information_schema',
                'topology'
            )

        order by
            n.nspname,
            p.proname
        """;
}