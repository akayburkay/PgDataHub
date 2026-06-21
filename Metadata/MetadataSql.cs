namespace PgDataHub.Metadata;

internal static class MetadataSql
{
    public const string Collections = """
        WITH collections AS (
            SELECT 
                table_schema, 
                table_name, 
                table_type
            FROM information_schema.tables
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'topology')
              AND table_name NOT IN (
                  'geometry_columns',
                  'geography_columns',
                  'spatial_ref_sys',
                  'raster_columns',
                  'raster_overviews'
              )

            UNION ALL

            SELECT 
                schemaname,
                matviewname,
                'MATERIALIZED VIEW'
            FROM pg_matviews
            WHERE schemaname NOT IN ('pg_catalog', 'information_schema', 'topology')
        ),

        feature_ids AS (
            SELECT
                n.nspname AS table_schema,
                c.relname AS table_name,
                a.attname AS feature_id
            FROM pg_index i
            JOIN pg_class c
                ON c.oid = i.indrelid
            JOIN pg_namespace n
                ON n.oid = c.relnamespace
            JOIN pg_attribute a
                ON a.attrelid = i.indrelid
               AND a.attnum = ANY(i.indkey)
            WHERE 
                (
                    i.indisprimary
                    OR (
                        i.indisunique
                        AND array_length(i.indkey, 1) = 1
                    )
                )
        )

        SELECT
            c.table_schema,
            c.table_name,
            c.table_type,
            g.f_geometry_column,
            g.type,
            g.srid,
            f.feature_id
        FROM collections c

        LEFT JOIN geometry_columns g
            ON g.f_table_schema = c.table_schema
           AND g.f_table_name = c.table_name

        LEFT JOIN feature_ids f
            ON f.table_schema = c.table_schema
           AND f.table_name = c.table_name

        ORDER BY
            c.table_schema,
            c.table_name;
        """;

    public const string Columns = """
        select
            n.nspname,
            c.relname,
            a.attname,
            format_type(a.atttypid, a.atttypmod),
            not a.attnotnull
        from pg_attribute a
        join pg_class c on c.oid = a.attrelid
        join pg_namespace n on n.oid = c.relnamespace
        where
            a.attnum > 0
            and not a.attisdropped
            and c.relkind in ('r','v','m')
            and n.nspname not in ('pg_catalog', 'information_schema', 'topology')
        order by
            n.nspname,
            c.relname,
            a.attnum;
        """;

    public const string Functions = """
        select
            n.nspname,
            p.proname,
            case
                when p.proretset then 'Rowset'
                when t.typname in ('json', 'jsonb') then 'Json'
                when t.typcategory = 'A' then 'Array'
                when t.typtype = 'c' then 'Composite'
                else 'Scalar'
            end,
            d.description
        from pg_proc p
        join pg_namespace n on n.oid = p.pronamespace
        join pg_type t on t.oid = p.prorettype
        left join pg_description d on d.objoid = p.oid and d.objsubid = 0
        left join pg_depend dep on dep.objid = p.oid and dep.deptype = 'e'
        left join pg_extension ext on ext.oid = dep.refobjid
        where
            p.prokind = 'f'
            and ext.extname is null
            and n.nspname not in ('pg_catalog', 'information_schema', 'topology')
            and (
                p.proretset
                or t.typname in ('json', 'jsonb')
                or t.typcategory = 'A'
                or t.typtype = 'c'
                or t.typcategory in ('N', 'S', 'B', 'D')
            )
        order by
            n.nspname,
            p.proname;
        """;

    public const string FunctionColumns = """
               select
            n.nspname,
            p.proname,
            a.attname,
            format_type(a.atttypid, a.atttypmod),
            a.attnum
        from pg_proc p
        join pg_namespace n on n.oid = p.pronamespace
        join pg_type t on t.oid = p.prorettype
        join pg_class c on c.oid = t.typrelid
        join pg_attribute a on a.attrelid = c.oid
        left join pg_depend dep on dep.objid = p.oid and dep.deptype = 'e'
        left join pg_extension ext on ext.oid = dep.refobjid
        where
            p.prokind = 'f'
            and ext.extname is null
            and n.nspname not in ('pg_catalog', 'information_schema', 'topology')
            and a.attnum > 0
            and not a.attisdropped
            and (
                p.proretset
                or t.typtype = 'c'
            )

        union all

        select
            n.nspname,
            p.proname,
            a.name,
            format_type(a.type_oid, null),
            a.ord
        from pg_proc p
        join pg_namespace n on n.oid = p.pronamespace
        left join pg_depend dep on dep.objid = p.oid and dep.deptype = 'e'
        left join pg_extension ext on ext.oid = dep.refobjid
        cross join lateral unnest(
            p.proargnames,
            p.proargmodes,
            p.proallargtypes
        ) with ordinality as a(name, mode, type_oid, ord)
        where
            p.prokind = 'f'
            and ext.extname is null
            and n.nspname not in ('pg_catalog', 'information_schema', 'topology')
            and a.mode = 't'

        order by
            1, 2, 5;
        """;

    public const string FunctionParameters = """
              select
            n.nspname,
            p.proname,
            a.name,
            format_type(a.type_oid, null)
        from pg_proc p
        join pg_namespace n
            on n.oid = p.pronamespace

        left join pg_depend dep
            on dep.objid = p.oid
           and dep.deptype = 'e'

        left join pg_extension ext
            on ext.oid = dep.refobjid

        cross join lateral unnest(
            p.proargnames,
            p.proargmodes,
            p.proallargtypes
        ) as a(name, mode, type_oid)

        where
            p.prokind = 'f'
            and ext.extname is null
            and n.nspname not in (
                'pg_catalog',
                'information_schema',
                'topology'
            )
            and a.mode in ('i', 'b', 'v')

        order by
            n.nspname,
            p.proname;
        """;
}